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
            // "break" breakpoint removes any stale capture config.
            var configKey = (Path.GetFullPath(filePath), line);
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
                if (bpResults[i].Line.HasValue)
                    entries[i] = entries[i] with { Line = bpResults[i].Line!.Value };
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
                _bpConfigs.Remove((Path.GetFullPath(file), entry.Line));
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
        var snapshot = new CaptureSnapshot(
            Index: ++_captureIndex,
            Reason: _lastStop!.Reason,
            ThreadId: _lastStop.ThreadId,
            FilePath: _lastStop.FilePath,
            Line: _lastStop.Line,
            Variables: GetVariablesForFrame(
                GetStackTrace(_activeThreadId ?? 1).First().Id,
                scope, depth),
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
    /// Race <see cref="_pendingStopTcs"/> against a timeout and user cancellation.
    /// Returns a <see cref="StopEvent"/> indicating whether a real stop occurred,
    /// the wait timed out, or the user cancelled.
    /// </summary>
    private async Task<StopEvent> WaitForStopInTimespanAsync(
        int timeoutSeconds,
        CancellationToken ct)
    {
        var tcs = Volatile.Read(ref _pendingStopTcs);
        var timeoutSpan = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : Timeout.InfiniteTimeSpan;
        var timeoutTask = Task.Delay(timeoutSpan);
        var cancelTask = Task.Delay(Timeout.Infinite, ct);

        _logger.LogInformation("WaitForStop: waiting (timeout={Timeout}s)...", timeoutSeconds);

        var completed = await Task.WhenAny(tcs.Task, timeoutTask, cancelTask);

        if (completed == tcs.Task)
        {
            _logger.LogInformation("WaitForStop: stop event received");
            if (_stateMachine.Current == SessionState.Exited) return LastStop;
            return BuildStopEvent(tcs.Task.Result);
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
        _stateMachine.TransitionTo(SessionState.Running);

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct);
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

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct);
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
        return BuildStopEvent(stopEvent);
    }

    public async Task<StopEvent> PauseAsync(CancellationToken ct = default)
    {
        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {_stateMachine.Current}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        _host!.SendRequestSync(new PauseRequest());

        var stopEvent = await stopTcs.Task.WaitAsync(ct);
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
        _host?.WaitForReader();
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

        // Check if this is a capture-action breakpoint
        if (e.Reason == StoppedEvent.ReasonValue.Breakpoint
            && _bpConfigs.Count > 0
            && ResolveBreakpointAction() is (true, true, var scope, var depth))
        {
            // Offload capture to thread pool — don't block the DAP reader.
            // DO NOT touch _pendingStopTcs — caller keeps waiting.
            var host = _host!;
            var threadId = e.ThreadId;
            _ = Task.Run(() =>
            {
                try
                {
                    CaptureState(scope, depth);
                    host.SendRequestSync(new ContinueRequest { ThreadId = threadId ?? 0 });
                    _stateMachine.TransitionTo(SessionState.Running);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Capture auto-continue failed");
                }
            });
            _logger.LogInformation("← OnStopped: auto-continue (capture), TCS not touched, thread={ThreadId}", e.ThreadId);
            return;
        }

        // Break-action or non-breakpoint stop: stay stopped, wake caller
        _lastStop = BuildStopEvent(e);
        var newTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Interlocked.Exchange(ref _pendingStopTcs, newTcs);
        old.TrySetResult(e);
        _logger.LogInformation("← OnStopped: TCS resolved (reason={Reason}, thread={ThreadId}), new TCS created",
            e.Reason, e.ThreadId);
    }

    private (bool IsCapture, bool ShouldCapture, string Scope, int Depth) ResolveBreakpointAction()
    {
        if (_activeThreadId is null) return (false, false, "all", 0);
        try
        {
            var frames = GetStackTrace(_activeThreadId.Value, 0, 1);
            if (frames.Count > 0 && frames[0].Source is not null
                && _bpConfigs.TryGetValue((Path.GetFullPath(frames[0].Source!), frames[0].Line), out var bpCfg))
            {
                return (bpCfg.Action == "capture", bpCfg.Action == "capture",
                    bpCfg.CaptureScope ?? "all", bpCfg.CaptureDepth);
            }
        }
        catch { /* ignore lookup errors on DAP reader thread */ }
        return (false, false, "all", 0);
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
        return new StopEvent(
            "stopped",
            e.ThreadId,
            e.AllThreadsStopped,
            e.Reason.ToString(),
            null,
            e.HitBreakpointIds?.FirstOrDefault() ?? 0,
            0);
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
