using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpBridge.State;
using SharpDbg.Infrastructure;
using SharpDbg.InMemory;

namespace SharpBridge.Services;

/// <summary>
/// Wraps the DAP debug adapter (SharpDbg) via DebugProtocolHost.
/// One instance per debugged process.
///
/// Architecture:
///   DebugProtocolHost.Run() runs its internal DAP message reader on a
///   background thread. SendRequestSync is thread-safe and can be called
///   from any thread. We call it directly from the MCP thread (Thread ③).
///
///   For async operations (continue/step/launch-stopAtEntry), we pre-register
///   a StoppedEvent handler whose TCS is swapped before each operation.
/// </summary>
public class DebugSession : IDisposable
{

    private readonly ILogger _logger;
    private SessionStateMachine _stateMachine = new SessionStateMachine();
    
    // ===================================================================
    // DAP Protocol Host
    // ===================================================================
    private DebugProtocolHost? _host;
    private IDisposable? _adapter;

    // ===================================================================
    // StoppedEvent TCS — swapped before each async operation
    // ===================================================================
    private TaskCompletionSource<StoppedEvent> _pendingStopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ===================================================================
    // Session state
    // ===================================================================
    public SessionState CurrentState => _stateMachine.Current;

    private StopEvent? _lastStop;
    private StopEvent LastStop => _lastStop
        ?? throw new InvalidOperationException("No stop event. Debugger may not be stopped.");
    private readonly List<string> _outputLog = new();
    private readonly Dictionary<string, List<BreakpointEntry>> _breakpointsByFile = new();
    private readonly List<BreakpointEntry> _functionBreakpoints = [];
    private int _nextBreakpointId = 1;
    private string? _adapterId;
    private List<ExceptionBreakpointsFilter>? _exceptionFilters;
    private int? _activeThreadId;
    private readonly List<CaptureSnapshot> _captures = [];
    private int _captureIndex;

    // ===================================================================
    // Session identity
    // ===================================================================
    public int? ProcessId { get; private set; }
    public string? ProcessName { get; private set; }

    // ===================================================================
    // Lifecycle callbacks
    // ===================================================================
    private readonly Action<int>? _onDisposed;
    private readonly Action<int, Exception>? _onError;
    private bool _cleanedUp;

    private static readonly Regex PidLogRegex = new(
        @"Process created suspended with PID:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ===================================================================
    // Construction (initialization is implicit)
    // ===================================================================

    public DebugSession(
        ILogger<DebugSession> logger,
        Action<int>? onDisposed = null,
        Action<int, Exception>? onError = null)
    {
        _logger = logger;
        _onDisposed = onDisposed;
        _onError = onError;

        // Build a logAction that captures PID from SharpDbg output
        var (input, output, disposable) = SharpDbgInMemory.NewDebugAdapterStreams(msg =>
        {
            // Try to extract PID from SharpDbg log output
            if (ProcessId is null)
            {
                var m = PidLogRegex.Match(msg);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pid))
                    ProcessId = pid;
            }
            _logger.LogDebug("SharpDbg: {Message}", msg);
        });
        _adapter = disposable;

        _host = new DebugProtocolHost(input, output, registerStandardHandlers: false);

        // Register events before Run()
        _host.RegisterEventType<StoppedEvent>(OnStopped);
        _host.RegisterEventType<ExitedEvent>(OnExited);
        _host.RegisterEventType<TerminatedEvent>(OnTerminated);
        _host.RegisterEventType<OutputEvent>(OnOutput);
        _host.RegisterEventType<ContinuedEvent>(e =>
            _logger.LogInformation($"← ContinuedEvent: thread={e.ThreadId}"));
        _host.RegisterEventType<InitializedEvent>(e =>
            _logger.LogInformation("← InitializedEvent"));

        _host.VerifySynchronousOperationAllowed();

        // Start the DAP message reader on a background thread
        _host.Run();

        // DAP handshake — call SendRequestSync directly (thread-safe)
        var initResponse = _host.SendRequestSync(new InitializeRequest
        {
            ClientID = "sharpbridge-mcp",
            ClientName = "SharpBridge",
            AdapterID = "sharpbridge",
            Locale = "en",
            LinesStartAt1 = true,
            ColumnsStartAt1 = true,
            PathFormat = InitializeArguments.PathFormatValue.Path,
            SupportsVariableType = true,
            SupportsVariablePaging = false,
            SupportsRunInTerminalRequest = false,
            SupportsMemoryReferences = false,
            SupportsProgressReporting = false,
        });

        _exceptionFilters = initResponse.ExceptionBreakpointFilters;

        _adapterId = "sharpdbg";
        _logger.LogInformation($"DAP initialized. Adapter: {_adapterId}");
    }

    // ===================================================================
    // Launch / Attach
    // ===================================================================

    public async Task LaunchAsync(
        string program,
        string[]? args = null,
        string? cwd = null,
        bool stopAtEntry = true,
        Dictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        // if (CurrentState != State.NotStarted)
        //     throw new InvalidOperationException("Session not in correct state for launch.");

        ProcessName = Path.GetFileNameWithoutExtension(program);

        var launchArgs = new Dictionary<string, JToken>
        {
            ["program"] = program,
            ["stopAtEntry"] = stopAtEntry,
            ["console"] = "internalConsole",
        };
        if (args is { Length: > 0 }) launchArgs["args"] = JToken.FromObject(args);
        if (cwd is not null) launchArgs["cwd"] = cwd;
        if (env is { Count: > 0 }) launchArgs["env"] = JToken.FromObject(env);

        _host!.SendRequestSync(new LaunchRequest
        {
            ConfigurationProperties = launchArgs
        });

        _stateMachine.TransitionTo(SessionState.Attaching);
        // Swap in a fresh stop TCS BEFORE configurationDone —
        // the StoppedEvent may fire as soon as the process starts.
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        _host.SendRequestSync(new ConfigurationDoneRequest());

        _stateMachine.TransitionTo(SessionState.Running);


        if (stopAtEntry)
        {
            // SharpDbg 0.1.4 does NOT send StoppedEvent after launch.
            // Try a brief wait in case a future version adds this, then fall back.
            try
            {
                await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                _logger.LogInformation("Launch: stopped at entry.");
                // The wait may have been resolved by the process exiting
                // (fast-exiting debuggees) — do not force Stopped in that case.
                if (_stateMachine.Current == SessionState.Exited)
                {
                    _logger.LogInformation("Launch: process exited before the entry stop — leaving state as Exited.");
                    return;
                }
                _stateMachine.TransitionTo(SessionState.Stopped);
                return;
            }
            catch (TimeoutException) { }

            _logger.LogInformation("Launch: no StoppedEvent — trying pause to force stopAtEntry.");
            await ForceStopAfterLaunch(ct);
        }
    }

    private async Task ForceStopAfterLaunch(CancellationToken ct)
    {
        var delays = new[] { 0, 200, 500 };
        for (int i = 0; i < delays.Length; i++)
        {
            if (i > 0) await Task.Delay(delays[i], ct);

            if (_stateMachine.Current is SessionState.Exited or SessionState.Stopped) return;

            var pauseTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pendingStopTcs, pauseTcs);

            _host!.SendRequestSync(new PauseRequest());

            try
            {
                await pauseTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                _logger.LogInformation("Pause succeeded.");
                // The pause wait may have been resolved by the process exiting.
                if (_stateMachine.Current is SessionState.Exited or SessionState.Stopped) return;
                _stateMachine.TransitionTo(SessionState.Stopped);
                return;
            }
            catch (TimeoutException) { }
        }

        if (_stateMachine.Current == SessionState.Running)
        {
            _logger.LogInformation("Pause did not respond — synthesizing stopped state.");
            _stateMachine.TransitionTo(SessionState.Stopped);
            _lastStop = new StopEvent("stopped", null, true, "entry", null, 0, 0)
            {
                Note = "Process is running (did not respond to pause). " +
                       "Set breakpoints and use debug_continue to reach them."
            };
        }
    }

    public async Task AttachAsync(int processId, CancellationToken ct = default)
    {
        // if (CurrentState != State.NotStarted)
        //     throw new InvalidOperationException("Session not in correct state for attach.");

        ProcessId = processId;
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(processId);
            ProcessName = proc.ProcessName;
        }
        catch
        {
            ProcessName = $"PID:{processId}";
        }

        // Attach is lazy: stores PID, actual attach happens at ConfigurationDone.
        // Breakpoints set between AttachRequest and ConfigurationDone will be
        // applied during the attach.
        _host!.SendRequestSync(new AttachRequest
        {
            ConfigurationProperties = new Dictionary<string, JToken>
            {
                ["processId"] = processId
            }
        });
        _stateMachine.TransitionTo(SessionState.Attaching);
    }

    // ===================================================================
    // Breakpoint Management
    // ===================================================================

    public record BreakpointEntry(
        int Id, string FilePath, int Line, int? Column,
        string? Condition, string? HitCondition,
        bool Verified, int? EndLine, int? EndColumn)
    {
        public bool Verified { get; set; } = Verified;
        public string? Message { get; set; }
        public string Action { get; set; } = "break";
        public string? CaptureScope { get; set; }
        public int CaptureDepth { get; set; }
        public string? FunctionName { get; set; }
    }

    private readonly Dictionary<(string File, int Line), BreakpointEntry> _bpConfigs = [];

    public IReadOnlyList<BreakpointEntry> SetBreakpoints(
        string filePath,
        params (int Line, int? Column, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth)[] breakpoints)
    {
        _breakpointsByFile.Remove(filePath);

        // Drop stale capture configs for this file — the set below replaces
        // all breakpoints in it, so old configs must not survive.
        var normalizedFile = NormalizePath(filePath);
        foreach (var staleKey in _bpConfigs.Keys.Where(k => k.File == normalizedFile).ToList())
            _bpConfigs.Remove(staleKey);

        var entries = new List<BreakpointEntry>();
        var sourceBreakpoints = new List<SourceBreakpoint>();

        foreach (var (line, col, cond, hitCond, action, captureScope, captureDepth) in breakpoints)
        {
            var entry = new BreakpointEntry(
                Id: _nextBreakpointId++,
                FilePath: filePath,
                Line: line,
                Column: col,
                Condition: cond,
                HitCondition: hitCond,
                Verified: false,
                EndLine: null,
                EndColumn: null)
            {
                Action = action,
                CaptureScope = captureScope,
                CaptureDepth = captureDepth
            };
            entries.Add(entry);

            // Track capture-action breakpoints for auto-capture on stop.
            // Always update the registry so re-setting a line as a plain
            // "break" breakpoint removes any stale capture config. Keys are
            // normalized to match the hit location SharpDbg reports in the
            // stopped event.
            var configKey = (normalizedFile, line);
            if (action == "capture")
                _bpConfigs[configKey] = entry;
            else
                _bpConfigs.Remove(configKey);

            var sbp = new SourceBreakpoint { Line = line };
            if (col.HasValue) sbp.Column = col.Value;
            if (cond is not null) sbp.Condition = cond;
            if (hitCond is not null) sbp.HitCondition = hitCond;
            sourceBreakpoints.Add(sbp);
        }

        _breakpointsByFile[filePath] = entries;

        var response = _host!.SendRequestSync(new SetBreakpointsRequest
        {
            Source = new Source { Path = filePath },
            Breakpoints = sourceBreakpoints
        });

        var bpResults = response.Breakpoints;
        if (bpResults is not null)
        {
            for (int i = 0; i < Math.Min(entries.Count, bpResults.Count); i++)
            {
                entries[i].Verified = bpResults[i].Verified;
                entries[i].Message = bpResults[i].Message;
                if (bpResults[i].Line.HasValue && bpResults[i].Line.Value != entries[i].Line)
                {
                    var oldKey = (normalizedFile, entries[i].Line);
                    entries[i] = entries[i] with { Line = bpResults[i].Line!.Value };
                    // Re-key the capture config so hit-location lookups still
                    // match when the adapter adjusts the line (e.g. moved to
                    // the next executable statement).
                    if (_bpConfigs.Remove(oldKey))
                        _bpConfigs[(normalizedFile, entries[i].Line)] = entries[i];
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Set function breakpoints. DAP SetFunctionBreakpoints REPLACES ALL function breakpoints.
    /// </summary>
    public IReadOnlyList<BreakpointEntry> SetFunctionBreakpoints(
        params (string Name, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth)[] breakpoints)
    {
        _functionBreakpoints.Clear();

        var entries = new List<BreakpointEntry>();
        var fnBreakpoints = new List<FunctionBreakpoint>();

        foreach (var (name, cond, hitCond, action, captureScope, captureDepth) in breakpoints)
        {
            var entry = new BreakpointEntry(
                Id: _nextBreakpointId++,
                FilePath: "", Line: 0, Column: null,
                Condition: cond, HitCondition: hitCond,
                Verified: false, EndLine: null, EndColumn: null)
            {
                FunctionName = name,
                Action = action,
                CaptureScope = captureScope,
                CaptureDepth = captureDepth
            };
            entries.Add(entry);

            var fbp = new FunctionBreakpoint { Name = name };
            if (cond is not null) fbp.Condition = cond;
            if (hitCond is not null) fbp.HitCondition = hitCond;
            fnBreakpoints.Add(fbp);
        }

        _functionBreakpoints.AddRange(entries);

        if (_host is not null)
        {
            var response = _host.SendRequestSync(new SetFunctionBreakpointsRequest
            {
                Breakpoints = fnBreakpoints
            });

            var bpResults = response.Breakpoints;
            if (bpResults is not null)
            {
                for (int i = 0; i < Math.Min(entries.Count, bpResults.Count); i++)
                {
                    entries[i].Verified = bpResults[i].Verified;
                    entries[i].Message = bpResults[i].Message;
                }
            }
        }

        return entries;
    }

    public bool RemoveBreakpoint(int id)
    {
        foreach (var (file, entries) in _breakpointsByFile)
        {
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entries.Remove(entry);
                // Drop any capture config for this breakpoint so a stale
                // auto-continue doesn't fire if the line is re-set as "break".
                _bpConfigs.Remove((NormalizePath(file), entry.Line));
                if (entries.Count == 0)
                {
                    _breakpointsByFile.Remove(file);
                    _host!.SendRequestSync(new SetBreakpointsRequest
                    {
                        Source = new Source { Path = file },
                        Breakpoints = new List<SourceBreakpoint>()
                    });
                }
                else
                {
                    SetBreakpoints(file, entries.Select(e =>
                        (e.Line, e.Column, e.Condition, e.HitCondition,
                         e.Action, e.CaptureScope, e.CaptureDepth)).ToArray());
                }
                return true;
            }
        }

        // Check function breakpoints
        var fnEntry = _functionBreakpoints.FirstOrDefault(e => e.Id == id);
        if (fnEntry is not null)
        {
            _functionBreakpoints.Remove(fnEntry);
            // Re-send remaining function breakpoints (DAP replaces-all semantics)
            var remaining = _functionBreakpoints
                .Select(e => new FunctionBreakpoint { Name = e.FunctionName!, Condition = e.Condition, HitCondition = e.HitCondition })
                .ToList();
            _host!.SendRequestSync(new SetFunctionBreakpointsRequest { Breakpoints = remaining });
            return true;
        }

        return false;
    }

    public IReadOnlyList<BreakpointEntry> GetAllBreakpoints()
        => _breakpointsByFile.Values.SelectMany(v => v)
            .Concat(_functionBreakpoints)
            .OrderBy(e => e.Id).ToList();

    public int BreakpointCount => _breakpointsByFile.Values.Sum(v => v.Count) + _functionBreakpoints.Count;

    // ===================================================================
    // Capture System
    // ===================================================================

    public CaptureSnapshot CaptureState(string scope = "all", int depth = 0)
    {
        EnsureStopped();

        // Location comes from the actual top frame — the ground truth for
        // "where the snapshot was taken", and it works even when no stop
        // event was ever processed (e.g. launch with pause-success).
        var frame = GetStackTrace(_activeThreadId ?? 1, 0, 1).FirstOrDefault();

        var snapshot = new CaptureSnapshot(
            Index: ++_captureIndex,
            Reason: _lastStop?.Reason,
            ThreadId: _lastStop?.ThreadId,
            FilePath: frame?.Source,
            Line: frame?.Line ?? 0,
            Variables: frame is null ? [] : GetVariablesForFrame(frame.Id, scope, depth),
            Timestamp: DateTime.UtcNow);
        _captures.Add(snapshot);
        return snapshot;
    }

    public IReadOnlyList<CaptureSnapshot> GetCaptures() => _captures;

    public void ClearCaptures()
    {
        _captures.Clear();
        _captureIndex = 0;
    }

    // ===================================================================
    // Exception Breakpoints
    // ===================================================================

    public IReadOnlyList<ExceptionBreakpointsFilter>? GetExceptionBreakpointFilters()
        => _exceptionFilters;

    public void SetExceptionBreakpoints(string[] filters)
    {
        _host!.SendRequestSync(new SetExceptionBreakpointsRequest
        {
            Filters = filters.ToList()
        });
        _logger.LogInformation($"Exception breakpoints set: [{string.Join(", ", filters)}]");
    }

    // ===================================================================
    // Execution Control
    // ===================================================================

    /// <summary>
    /// Race <paramref name="stopTcs"/> (already swapped into <see cref="_pendingStopTcs"/>)
    /// against a timeout and user cancellation.
    /// Returns a <see cref="StopEvent"/> indicating whether a real stop occurred,
    /// the wait timed out, or the user cancelled.
    /// </summary>
    private async Task<StopEvent> WaitForStopInTimespanAsync(
        int timeoutSeconds,
        CancellationToken ct,
        TaskCompletionSource<StoppedEvent> stopTcs)
    {
        var timeoutSpan = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : Timeout.InfiniteTimeSpan;
        var timeoutTask = Task.Delay(timeoutSpan);
        var cancelTask = Task.Delay(Timeout.Infinite, ct);

        _logger.LogInformation("WaitForStop: waiting (timeout={Timeout}s)...", timeoutSeconds);

        var completed = await Task.WhenAny(stopTcs.Task, timeoutTask, cancelTask);

        if (completed == stopTcs.Task)
        {
            _logger.LogInformation("WaitForStop: stop event received");
            if (_stateMachine.Current == SessionState.Exited) return LastStop;
            return BuildStopEvent(stopTcs.Task.Result);
        }

        if (_stateMachine.Current == SessionState.Exited)
        {
            _logger.LogInformation("WaitForStop: process exited during wait");
            return LastStop;
        }

        if (completed == cancelTask)
        {
            _logger.LogInformation("WaitForStop: cancelled by user");
            return new StopEvent("running", null, null, "cancelled", null, 0, 0)
            {
                Note = "Wait was cancelled."
            };
        }

        _logger.LogInformation("WaitForStop: timed out");
        return new StopEvent("running", null, null, "timeout", null, 0, 0)
        {
            Note = "Process is in running. Use debug_wait or debug_pause to interrupt."
        };
    }

    public async Task<StopEvent> ContinueAndWaitAsync(
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        if (BreakpointCount == 0 && timeoutSeconds == 0)
        {
            throw new InvalidOperationException(
                "No breakpoints set and timeout is disabled (0 = infinite). " +
                "Set a breakpoint with breakpoint_set first, " +
                "or specify a timeout value (e.g. timeout=30).");
        }

        if (_stateMachine.Current != SessionState.Stopped && _stateMachine.Current != SessionState.Attaching)
            throw new InvalidOperationException($"Cannot continue: debugger state is {_stateMachine.Current}.");

        // Swap in a fresh stop TCS BEFORE sending the command: any stop that
        // arrives after this point resolves THIS TCS, so no stop can be missed
        // between the command and the wait (same pattern as StepAsync).
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        // Send the right command to get the process running
        if (_stateMachine.Current == SessionState.Attaching)
        {
            _host!.SendRequestSync(new ConfigurationDoneRequest());
            if (ProcessId.HasValue)
            {
                try { await DiagnosticClientHelper.DiagnosticClientResumeRuntime(ProcessId.Value); }
                catch (ServerNotAvailableException) { }
            }
        }
        else
        {
            _host!.SendRequestSync(new ContinueRequest { ThreadId = _lastStop?.ThreadId ?? 0 });
        }

        // A stop may have arrived while the command was in flight (e.g. a
        // breakpoint hit immediately after resume). The TCS check is exact:
        // OnStopped resolves exactly the TCS swapped in above.
        if (stopTcs.Task.IsCompleted)
            return LastStop;

        _stateMachine.TransitionTo(SessionState.Running);

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct, stopTcs);
    }

    /// <summary>
    /// Wait for a stop event without sending any DAP command.
    /// Only valid when the process is already running.
    /// </summary>
    public async Task<StopEvent> WaitAndWaitAsync(
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot wait: debugger state is {_stateMachine.Current}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        // A stop may have arrived between the state guard and the swap —
        // report it instead of waiting for the next stop.
        if (stopTcs.Task.IsCompleted)
            return LastStop;

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct, stopTcs);
    }

    public async Task<StopEvent> StepAsync(
        string type, int? threadId = null, CancellationToken ct = default)
    {
        if (_stateMachine.Current != SessionState.Stopped)
            throw new InvalidOperationException($"Cannot step: debugger state is {_stateMachine.Current}.");

        _stateMachine.TransitionTo(SessionState.Running);
        var tid = threadId ?? _lastStop?.ThreadId ?? 1;

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        switch (type)
        {
            case "in": _host!.SendRequestSync(new StepInRequest(tid)); break;
            case "out": _host!.SendRequestSync(new StepOutRequest(tid)); break;
            default: _host!.SendRequestSync(new NextRequest(tid)); break;
        }

        var stopEvent = await stopTcs.Task.WaitAsync(ct);
        if (_stateMachine.Current == SessionState.Exited) return LastStop;
        return BuildStopEvent(stopEvent);
    }

    public async Task<StopEvent> PauseAsync(CancellationToken ct = default)
    {
        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {_stateMachine.Current}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        // A breakpoint stop may have arrived before the pause was processed.
        if (stopTcs.Task.IsCompleted)
            return LastStop;

        _host!.SendRequestSync(new PauseRequest());

        var stopEvent = await stopTcs.Task.WaitAsync(ct);
        if (_stateMachine.Current == SessionState.Exited) return LastStop;
        return BuildStopEvent(stopEvent);
    }

    // ===================================================================
    // Inspection
    // ===================================================================

    public List<ThreadInfo> GetThreads()
    {
        EnsureStopped();
        var response = _host!.SendRequestSync(new ThreadsRequest());
        return response.Threads.Select(t => new ThreadInfo(
            t.Id, t.Name, t.Id == _activeThreadId)).ToList();
    }

    public List<StackFrameInfo> GetStackTrace(int threadId, int startFrame = 0, int? levels = null)
    {
        EnsureStopped();
        var response = _host!.SendRequestSync(new StackTraceRequest
        {
            ThreadId = threadId,
            StartFrame = startFrame,
            Levels = levels
        });

        return response.StackFrames.Select(f => new StackFrameInfo(
            f.Id,
            f.Name,
            f.Source?.Path,
            f.Line,
            f.Column,
            f.EndLine ?? 0,
            f.EndColumn ?? 0)).ToList();
    }

    public List<VariableInfo> GetVariablesForFrame(
        int frameId,
        string scope = "all",
        int depth = 0,
        IReadOnlySet<string>? expand = null)
    {
        EnsureStopped();
        var scopes = GetScopes(frameId);
        if (scopes.Count == 0) return [];

        List<ScopeInfo> selected = scope switch
        {
            "locals" => scopes.Where(s => s.Name == "Locals").ToList(),
            "arguments" => scopes.Where(s => s.Name == "Arguments").ToList(),
            "all" => scopes.Where(s => s.Name is "Locals" or "Arguments").ToList(),
            _ => throw new ArgumentException(
                $"Unknown scope '{scope}'. Use 'locals', 'arguments', or 'all'.")
        };

        if (selected.Count == 0)
            selected.Add(scopes[0]); // fallback

        var allVariables = new List<VariableInfo>();
        foreach (var s in selected)
        {
            var vars = ExpandVariables(s.VariablesReference);
            allVariables.AddRange(vars);
        }

        if (depth > 0)
        {
            for (int i = 0; i < allVariables.Count; i++)
            {
                var v = allVariables[i];
                if (v.VariablesReference > 0 &&
                    (expand is null || expand.Count == 0 || expand.Contains(v.Name)))
                {
                    var children = ExpandVariablesRecursive(v.VariablesReference, depth - 1, expand);
                    allVariables[i] = v with { Children = children };
                }
            }
        }

        return allVariables;
    }

    private List<VariableInfo> ExpandVariablesRecursive(
        int variablesReference, int remainingDepth, IReadOnlySet<string>? expand)
    {
        var children = ExpandVariables(variablesReference);
        if (remainingDepth <= 0) return children;

        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.VariablesReference > 0 &&
                (expand is null || expand.Count == 0 || expand.Contains(c.Name)))
            {
                var grandChildren = ExpandVariablesRecursive(
                    c.VariablesReference, remainingDepth - 1, expand);
                children[i] = c with { Children = grandChildren };
            }
        }
        return children;
    }

    public List<VariableInfo> ExpandVariables(int variablesReference)
    {
        EnsureStopped();
        var response = _host!.SendRequestSync(new VariablesRequest
        {
            VariablesReference = variablesReference
        });

        return response.Variables.Select(v => new VariableInfo(
            v.Name, v.Value, v.Type, v.VariablesReference,
            v.EvaluateName, v.IndexedVariables, v.NamedVariables)).ToList();
    }

    private List<ScopeInfo> GetScopes(int frameId)
    {
        var response = _host!.SendRequestSync(new ScopesRequest { FrameId = frameId });
        return response.Scopes.Select(s => new ScopeInfo(s.Name, s.VariablesReference, s.Expensive)).ToList();
    }

    public async Task<EvalResult> EvaluateAsync(string expression, int? frameId = null)
    {
        EnsureStopped();
        var response = _host!.SendRequestSync(new EvaluateRequest
        {
            Expression = expression,
            FrameId = frameId,
            Context = EvaluateArguments.ContextValue.Repl
        });
        return new EvalResult(response.Result, response.Type, response.VariablesReference);
    }

    public ExceptionDetail? GetExceptionInfo(int? threadId = null)
    {
        EnsureStopped();
        try
        {
            var response = _host!.SendRequestSync(new ExceptionInfoRequest
            {
                ThreadId = threadId ?? LastStop.ThreadId ?? 1
            });
            return new ExceptionDetail(
                response.ExceptionId, response.Description, response.BreakMode.ToString(),
                response.Details?.Message, response.Details?.TypeName,
                response.Details?.FullTypeName, response.Details?.StackTrace,
                response.Details?.FormattedDescription);
        }
        catch { return null; }
    }

    // ===================================================================
    // Disconnect
    // ===================================================================

    public void Disconnect(bool terminateDebuggee = true)
    {
        if (_stateMachine.Current == SessionState.Detached) return;

        // If the process already exited, the adapter session is over — skip
        // the DisconnectRequest entirely. Sending one after OnExited would
        // block forever: Cleanup() already stopped the reader thread, so no
        // response would ever be dispatched.
        if (_stateMachine.Current == SessionState.Exited)
        {
            Cleanup();
            return;
        }

        try
        {
            _host!.SendRequestSync(new DisconnectRequest { TerminateDebuggee = terminateDebuggee });
        }
        catch { }
        Cleanup();
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _host?.Stop();
        try
        {
            _host?.WaitForReader();
        }
        catch
        {
            // OnExited/OnTerminated run on the reader thread itself, where
            // joining is forbidden — the host is already stopped, so the
            // reader drains on its own.
        }
        _adapter?.Dispose();
        _host = null;

        if (ProcessId.HasValue)
            _onDisposed?.Invoke(ProcessId.Value);
    }

    // ===================================================================
    // Event handlers (on host's internal reader thread)
    // ===================================================================

    private void OnStopped(StoppedEvent e)
    {
        _logger.LogInformation("→ OnStopped: reason={Reason}, thread={ThreadId}, state={State}",
            e.Reason, e.ThreadId, _stateMachine.Current);

        _activeThreadId = e.ThreadId;
        _stateMachine.TransitionTo(SessionState.Stopped);

        // Record the stop before any branching: capture snapshots, waiters and
        // step/pause re-checks all rely on _lastStop being set for every stop.
        // Assignments happen on the reader thread while the process is frozen
        // (no Continue has been sent), so there is no concurrent writer.
        _lastStop = BuildStopEvent(e);

        // Capture-action breakpoints auto-capture and continue without waking
        // the caller. Resolution must NOT issue DAP requests here — OnStopped
        // runs on the DAP dispatcher thread where SendRequestSync throws by
        // design. SharpDbg delivers the hit location in the event itself.
        if (e.Reason == StoppedEvent.ReasonValue.Breakpoint && _bpConfigs.Count > 0)
        {
            if (TryResolveCapture(e) is { } capture)
            {
                // Offload capture to thread pool — don't block the DAP reader.
                // DO NOT touch _pendingStopTcs — the caller keeps waiting and
                // the next stop (or exit) resolves it.
                _ = Task.Run(() => RunCaptureAndContinueAsync(e.ThreadId, capture.Scope, capture.Depth));
                _logger.LogInformation("← OnStopped: auto-continue (capture), TCS not touched, thread={ThreadId}", e.ThreadId);
                return;
            }

            if (!TryGetHitLocation(e, out _, out _))
            {
                // Adapter sent no hit location (format change?) — the safest
                // failure mode is an explicit stop, not a silent continue.
                _lastStop = _lastStop with
                {
                    Note = "Stopped at a breakpoint without a hit location, so a capture config " +
                           "could not be matched — stopping instead of auto-capturing."
                };
                _logger.LogWarning("Stopped event without hit location while capture breakpoints are configured; treating as a plain break.");
            }
        }

        // Break-action or non-breakpoint stop: stay stopped, wake caller
        var newTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Interlocked.Exchange(ref _pendingStopTcs, newTcs);
        old.TrySetResult(e);
        _logger.LogInformation("← OnStopped: TCS resolved (reason={Reason}, thread={ThreadId}), new TCS created",
            e.Reason, e.ThreadId);
    }

    /// <summary>
    /// Resolve whether a breakpoint stop is a capture-action breakpoint, using
    /// only the hit location SharpDbg embeds in the stopped event. Pure
    /// in-memory lookup — safe on the dispatcher thread (no DAP requests).
    /// Returns null when the event carries no location or no config matches.
    /// </summary>
    private CaptureResolution? TryResolveCapture(StoppedEvent e)
    {
        if (!TryGetHitLocation(e, out var file, out var line))
            return null;

        return _bpConfigs.TryGetValue((NormalizePath(file), line), out var cfg)
            && cfg.Action == "capture"
            ? new CaptureResolution(cfg.CaptureScope ?? "all", cfg.CaptureDepth)
            : null;
    }

    /// <summary>
    /// Read the hit location SharpDbg attaches to breakpoint stopped events
    /// (source/line/column in the event's AdditionalProperties). Null-safe:
    /// pause and exception stops carry no location.
    /// </summary>
    private static bool TryGetHitLocation(StoppedEvent e, out string file, out int line)
    {
        file = "";
        line = 0;
        try
        {
            var props = ReadAdditionalProperties(e);
            if (props is null
                || !props.TryGetValue("source", out var sourceToken)
                || sourceToken is not JObject source
                || source["path"]?.Value<string>() is not { Length: > 0 } path)
            {
                return false;
            }

            file = path;
            line = props.TryGetValue("line", out var lineToken) ? lineToken.Value<int>() : 0;
            return line > 0;
        }
        catch
        {
            // Adapter format changed — caller decides the fallback behavior.
            return false;
        }
    }

    private static readonly PropertyInfo? AdditionalPropertiesProperty =
        typeof(ProtocolObject).GetProperty(
            "AdditionalProperties",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static Dictionary<string, JToken>? ReadAdditionalProperties(DebugEvent e)
        => AdditionalPropertiesProperty?.GetValue(e) as Dictionary<string, JToken>;

    /// <summary>
    /// Normalize a source path for capture-config lookup: absolute and, on
    /// Windows, case-insensitive — the adapter may report paths with different
    /// casing than the one the agent used to set the breakpoint.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private readonly record struct CaptureResolution(string Scope, int Depth);

    private void RunCaptureAndContinueAsync(int? threadId, string scope, int depth)
    {
        try
        {
            CaptureState(scope, depth);

            var host = _host;
            if (host is null || _stateMachine.Current is SessionState.Exited or SessionState.Detached)
            {
                _logger.LogWarning("Capture recorded but the session is no longer active — skipping auto-continue.");
                return;
            }

            host.SendRequestSync(new ContinueRequest { ThreadId = threadId ?? 0 });
            _stateMachine.TransitionTo(SessionState.Running);
        }
        catch (Exception ex)
        {
            // The debuggee may be left paused — log it so the hang is
            // diagnosable instead of silent.
            _logger.LogError(ex, "Capture auto-continue failed; the debuggee may remain paused");
        }
    }

    private void OnExited(ExitedEvent e)
    {
        _stateMachine.TransitionTo(SessionState.Exited);
        _lastStop = new StopEvent("exited", null, null, "exited", null, 0, 0)
        {
            ExitCode = e.ExitCode
        };
        CompletePendingStopTcs();
        _logger.LogInformation($"← ExitedEvent: code={e.ExitCode}");
        Cleanup();
    }

    private void OnTerminated(TerminatedEvent e)
    {
        _stateMachine.TransitionTo(SessionState.Exited);
        CompletePendingStopTcs();
        _logger.LogInformation("← TerminatedEvent");
        Cleanup();
    }

    private void CompletePendingStopTcs()
    {
        var newTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Interlocked.Exchange(ref _pendingStopTcs, newTcs);
        old.TrySetResult(
            new StoppedEvent(reason: StoppedEvent.ReasonValue.Breakpoint));
        _logger.LogInformation("CompletePendingStopTcs: TCS resolved (synthetic Breakpoint), state={State}",
            _stateMachine.Current);
    }

    private void OnOutput(OutputEvent e)
    {
        _outputLog.Add(e.Output ?? "");
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    public void DrainPendingEvents()
    {
        // With DebugProtocolHost, events are processed synchronously in callbacks.
        // State transitions happen inline — no event channel to drain.
    }

    private void EnsureStopped()
    {
        if (_stateMachine.Current != SessionState.Stopped)
            throw new InvalidOperationException(
                $"Debugger is not stopped (state: {_stateMachine.Current}). Use debug_state first.");
    }

    private StopEvent BuildStopEvent(StoppedEvent e)
    {
        TryGetHitLocation(e, out var file, out var line);
        return new StopEvent(
            "stopped",
            e.ThreadId,
            e.AllThreadsStopped,
            e.Reason.ToString(),
            file,
            line,
            0)
        {
            HitBreakpointIds = e.HitBreakpointIds
        };
    }
}

// ===================================================================
// Data Types
// ===================================================================

public record StopEvent(
    string Status,
    int? ThreadId,
    bool? AllThreadsStopped,
    string? Reason,
    string? FilePath,
    int Line,
    int Column)
{
    public int? ExitCode { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<int>? HitBreakpointIds { get; init; }
}

public record ThreadInfo(int Id, string Name, bool IsActive);
public record StackFrameInfo(
    int Id, string Name, string? Source,
    int Line, int Column, int EndLine, int EndColumn);
public record ScopeInfo(string Name, int VariablesReference, bool Expensive);
public record VariableInfo(
    string Name, string Value, string? Type, int VariablesReference,
    string? EvaluateName, int? IndexedVariables, int? NamedVariables)
{
    public List<VariableInfo>? Children { get; init; }
}
public record EvalResult(string Result, string? Type, int VariablesReference);
public record ExceptionDetail(
    string ExceptionId, string Description, string BreakMode,
    string? Message, string? TypeName, string? FullTypeName,
    string? StackTrace, string? FormattedDescription);

public record CaptureSnapshot(
    int Index,
    string? Reason,
    int? ThreadId,
    string? FilePath,
    int Line,
    IReadOnlyList<VariableInfo> Variables,
    DateTime Timestamp);
