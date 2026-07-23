using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
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
    public enum State { NotStarted, Running, Stopped, Exited }
    public State CurrentState { get; private set; } = State.NotStarted;

    private StopEvent? _lastStop;
    private StopEvent LastStop => _lastStop
        ?? throw new InvalidOperationException("No stop event. Debugger may not be stopped.");
    private readonly List<string> _outputLog = new();
    private readonly Dictionary<string, List<BreakpointEntry>> _breakpointsByFile = new();
    private int _nextBreakpointId = 1;
    private string? _adapterId;
    private List<ExceptionBreakpointsFilter>? _exceptionFilters;
    private int? _activeThreadId;
    private readonly List<CaptureSnapshot> _captures = [];
    private int _captureIndex;
    private bool _shouldAutoContinue;

    // ===================================================================
    // Session identity
    // ===================================================================
    public int? ProcessId { get; private set; }
    public string? ProcessName { get; private set; }
    public bool IsAttached { get; private set; }

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
        Action<int>? onDisposed = null,
        Action<int, Exception>? onError = null)
    {
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
            // Forward to OnLog for external consumers
            OnLog?.Invoke(msg);
        });
        _adapter = disposable;

        _host = new DebugProtocolHost(input, output, registerStandardHandlers: false);

        // Register events before Run()
        _host.RegisterEventType<StoppedEvent>(OnStopped);
        _host.RegisterEventType<ExitedEvent>(OnExited);
        _host.RegisterEventType<TerminatedEvent>(OnTerminated);
        _host.RegisterEventType<OutputEvent>(OnOutput);
        _host.RegisterEventType<ContinuedEvent>(e =>
            LogInfo($"← ContinuedEvent: thread={e.ThreadId}"));
        _host.RegisterEventType<InitializedEvent>(e =>
            LogInfo("← InitializedEvent"));

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
        LogInfo($"DAP initialized. Adapter: {_adapterId}");
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
        if (CurrentState != State.NotStarted)
            throw new InvalidOperationException("Session not in correct state for launch.");

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

        // Swap in a fresh stop TCS BEFORE configurationDone —
        // the StoppedEvent may fire as soon as the process starts.
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        _host.SendRequestSync(new ConfigurationDoneRequest());

        CurrentState = State.Running;

        if (stopAtEntry)
        {
            // SharpDbg 0.1.4 does NOT send StoppedEvent after launch.
            // Try a brief wait in case a future version adds this, then fall back.
            try
            {
                await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                LogInfo("Launch: stopped at entry.");
                IsAttached = true;
                return;
            }
            catch (TimeoutException) { }

            LogInfo("Launch: no StoppedEvent — trying pause to force stopAtEntry.");
            await ForceStopAfterLaunch(ct);
        }

        IsAttached = true;
    }

    private async Task ForceStopAfterLaunch(CancellationToken ct)
    {
        var delays = new[] { 0, 200, 500 };
        for (int i = 0; i < delays.Length; i++)
        {
            if (i > 0) await Task.Delay(delays[i], ct);

            if (CurrentState is State.Exited or State.Stopped) return;

            var pauseTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pendingStopTcs, pauseTcs);

            _host!.SendRequestSync(new PauseRequest());

            try
            {
                await pauseTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                LogInfo("Pause succeeded.");
                return;
            }
            catch (TimeoutException) { }
        }

        if (CurrentState == State.Running)
        {
            LogInfo("Pause did not respond — synthesizing stopped state.");
            CurrentState = State.Stopped;
            _lastStop = new StopEvent("stopped", null, true, "entry", null, 0, 0)
            {
                Note = "Process is running (did not respond to pause). " +
                       "Set breakpoints and use debug_continue to reach them."
            };
        }
    }

    public async Task AttachAsync(int processId, CancellationToken ct = default)
    {
        if (CurrentState != State.NotStarted)
            throw new InvalidOperationException("Session not in correct state for attach.");

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

        _host.SendRequestSync(new ConfigurationDoneRequest());

        // After attach, DebugActiveProcess(pid, false) suspends all threads.
        CurrentState = State.Stopped;
        IsAttached = true;
        _lastStop = new StopEvent("stopped", null, true, "attach", null, 0, 0)
        {
            Note = "Attached to process. All threads suspended. Set breakpoints and use debug_continue."
        };
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
        public bool Capture { get; set; }
        public string? CaptureScope { get; set; }
        public int CaptureDepth { get; set; }
    }

    private readonly Dictionary<(string File, int Line), BreakpointEntry> _bpConfigs = [];

    public IReadOnlyList<BreakpointEntry> SetBreakpoints(
        string filePath,
        params (int Line, int? Column, string? Condition, string? HitCondition,
                string Action, bool Capture, string? CaptureScope, int CaptureDepth)[] breakpoints)
    {
        _breakpointsByFile.Remove(filePath);

        var entries = new List<BreakpointEntry>();
        var sourceBreakpoints = new List<SourceBreakpoint>();

        foreach (var (line, col, cond, hitCond, action, capture, captureScope, captureDepth) in breakpoints)
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
                Capture = capture,
                CaptureScope = captureScope,
                CaptureDepth = captureDepth
            };
            entries.Add(entry);
            if (capture) _bpConfigs[(Path.GetFullPath(filePath), line)] = entry;

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

    public bool RemoveBreakpoint(int id)
    {
        foreach (var (file, entries) in _breakpointsByFile)
        {
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entries.Remove(entry);
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
                         "break", false, (string?)null, 0)).ToArray());
                }
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<BreakpointEntry> GetAllBreakpoints()
        => _breakpointsByFile.Values.SelectMany(v => v).OrderBy(e => e.Id).ToList();

    public int BreakpointCount => _breakpointsByFile.Values.Sum(v => v.Count);

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
        LogInfo($"Exception breakpoints set: [{string.Join(", ", filters)}]");
    }

    // ===================================================================
    // Execution Control
    // ===================================================================

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

        if (CurrentState != State.Stopped)
            throw new InvalidOperationException($"Cannot continue: debugger state is {CurrentState}.");

        using var totalCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, totalCts.Token);

        StoppedEvent? stopEvent = null;
        do
        {
            CurrentState = State.Running;

            var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

            _host!.SendRequestSync(new ContinueRequest { ThreadId = LastStop.ThreadId ?? 0 });

            try
            {
                stopEvent = await stopTcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (CurrentState == State.Exited)
                    return LastStop;
                LogInfo("Continue timed out — pausing.");
                return await PauseAndReturn(ct);
            }

            // On MCP thread — safe to query debugger to determine go/break + capture
            if (CurrentState == State.Stopped && _shouldAutoContinue)
            {
                var (isGo, shouldCapture, scope, depth) = ResolveBreakpointAction();
                if (shouldCapture)
                    CaptureState(scope, depth);
                _shouldAutoContinue = isGo;
            }
        } while (_shouldAutoContinue && CurrentState == State.Stopped);

        if (CurrentState == State.Exited)
            return LastStop;
        return BuildStopEvent(stopEvent);
    }

    public async Task<StopEvent> StepAsync(
        string type, int? threadId = null, CancellationToken ct = default)
    {
        if (CurrentState != State.Stopped)
            throw new InvalidOperationException($"Cannot step: debugger state is {CurrentState}.");

        CurrentState = State.Running;
        var tid = threadId ?? LastStop.ThreadId ?? 1;

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
        if (CurrentState != State.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {CurrentState}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        _host!.SendRequestSync(new PauseRequest());

        var stopEvent = await stopTcs.Task.WaitAsync(ct);
        return BuildStopEvent(stopEvent);
    }

    private async Task<StopEvent> PauseAndReturn(CancellationToken ct)
    {
        try
        {
            return await PauseAsync(ct);
        }
        catch
        {
            if (CurrentState == State.Running)
            {
                CurrentState = State.Stopped;
                _lastStop = new StopEvent("stopped", null, true, "pause", null, 0, 0)
                {
                    Note = "Unable to pause. Process may be blocked in native code."
                };
            }
            return LastStop;
        }
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
        if (CurrentState == State.NotStarted) return;
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

        CurrentState = State.NotStarted;
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
        CurrentState = State.Stopped;
        _lastStop = BuildStopEvent(e);
        _activeThreadId = e.ThreadId;

        // Flag for auto-continue: ContinueAndWaitAsync will decide on MCP thread
        _shouldAutoContinue = e.Reason == StoppedEvent.ReasonValue.Breakpoint;

        var old = Interlocked.Exchange(ref _pendingStopTcs,
            new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult(e);
        LogInfo($"← StoppedEvent: reason={e.Reason}, thread={e.ThreadId}" +
            (_shouldAutoContinue ? " (auto-continue)" : ""));
    }

    private (bool IsGo, bool ShouldCapture, string Scope, int Depth) ResolveBreakpointAction()
    {
        if (_activeThreadId is null) return (false, false, "all", 0);
        try
        {
            var frames = GetStackTrace(_activeThreadId.Value, 0, 1);
            if (frames.Count > 0 && frames[0].Source is not null)
            {
                if (frames[0].Source is not null
                    && _bpConfigs.TryGetValue((Path.GetFullPath(frames[0].Source!), frames[0].Line), out var bpCfg))
                    return (bpCfg.Action == "go", bpCfg.Capture,
                        bpCfg.CaptureScope ?? "all", bpCfg.CaptureDepth);
            }
        }
        catch { /* ignore lookup errors on MCP thread */ }
        return (false, false, "all", 0);
    }

    private void OnExited(ExitedEvent e)
    {
        CurrentState = State.Exited;
        _shouldAutoContinue = false;
        _lastStop = new StopEvent("exited", null, null, "exited", null, 0, 0)
        {
            ExitCode = e.ExitCode
        };
        CompletePendingStopTcs();
        LogInfo($"← ExitedEvent: code={e.ExitCode}");
        Cleanup();
    }

    private void OnTerminated(TerminatedEvent e)
    {
        CurrentState = State.Exited;
        _shouldAutoContinue = false;
        CompletePendingStopTcs();
        LogInfo("← TerminatedEvent");
        Cleanup();
    }

    private void CompletePendingStopTcs()
    {
        var old = Interlocked.Exchange(ref _pendingStopTcs,
            new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult(
            new StoppedEvent(reason: StoppedEvent.ReasonValue.Breakpoint));
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
        if (CurrentState != State.Stopped)
            throw new InvalidOperationException(
                $"Debugger is not stopped (state: {CurrentState}). Use debug_state first.");
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

    // ===================================================================
    // Logging
    // ===================================================================

    public event Action<string>? OnLog;

    private void LogInfo(string msg) => OnLog?.Invoke($"[DebugSession] {msg}");
}

// ===================================================================
// Data Types
// ===================================================================

public enum DebuggerState { NotStarted, Running, Stopped, Exited }

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
