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
    // Per-session gate — serializes all tool calls that touch the DAP
    // connection so concurrent MCP calls cannot interleave requests or
    // race the state machine. Capture auto-continue also takes this gate.
    // ===================================================================
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    /// <summary>
    /// Upper bound for buffered SharpDbg output lines. Prevents unbounded
    /// memory growth on long-running sessions with chatty debuggees.
    /// </summary>
    public const int MaxOutputLogLines = 5000;

    /// <summary>
    /// Runs <paramref name="action"/> under the per-session gate so that at
    /// most one tool call touches the DAP connection at a time. Also guards
    /// against calling into a cleaned-up session (host already disposed).
    /// </summary>
    public async Task<T> WithSessionLockAsync<T>(Func<ValueTask<T>> action)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_host is null)
                throw new InvalidOperationException(
                    "The debug session is no longer active (process exited or disconnected). Start a new session.");
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ===================================================================
    // StoppedEvent TCS — swapped before each async operation
    // ===================================================================
    private TaskCompletionSource<StoppedEvent> _pendingStopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ===================================================================
    // Stop ledger — detects stops that occur while no tool call is waiting
    // ===================================================================
    private long _stopSequence;
    private long _lastObservedSeq;

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

    /// <summary>Parses SharpDbg's per-module symbol-load log lines, e.g.
    /// "  Symbols loaded for TestDebuggee.dll" / "  No symbols found for X.dll".</summary>
    private static readonly Regex SymbolStatusRegex = new(
        @"^\s*(No symbols found|Symbols loaded) for (.+)\.dll$",
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

            // Track per-module symbol state from SharpDbg's log lines, so
            // breakpoint failures can be attributed (missing vs stale PDB).
            var sym = SymbolStatusRegex.Match(msg);
            if (sym.Success)
                _moduleSymbols[sym.Groups[2].Value] = !sym.Groups[1].Value.StartsWith("No");

            _logger.LogDebug("SharpDbg: {Message}", msg);
        });
        _adapter = disposable;

        _host = new DebugProtocolHost(input, output, registerStandardHandlers: false);

        // Register events before Run()
        _host.RegisterEventType<StoppedEvent>(OnStopped);
        _host.RegisterEventType<BreakpointEvent>(OnBreakpointChanged);
        _host.RegisterEventType<ExitedEvent>(OnExited);
        _host.RegisterEventType<TerminatedEvent>(OnTerminated);
        _host.RegisterEventType<OutputEvent>(OnOutput);
        _host.RegisterEventType<ContinuedEvent>(e =>
            _logger.LogInformation($"← ContinuedEvent: thread={e.ThreadId}"));
        _host.RegisterEventType<InitializedEvent>(e =>
            _logger.LogInformation("← InitializedEvent"));
        _host.RegisterEventType<ModuleEvent>(OnModuleChanged);

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

        // New process lifecycle — drop any stop ledger state from a previous
        // process so its stops cannot surface in this session.
        ResetStopLedger();

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

        // Declare Running BEFORE ConfigurationDone: a stop (e.g. a breakpoint
        // hit right after resume) can arrive while the command is in flight,
        // and OnStopped must never see the session as Attaching.
        _stateMachine.TransitionTo(SessionState.Running);
        try
        {
            _host.SendRequestSync(new ConfigurationDoneRequest());
        }
        catch
        {
            // Only roll back when no event changed the state — the debuggee
            // may already be running, stopped, or exited (real states that
            // must not be overwritten).
            if (_stateMachine.Current == SessionState.Running)
                _stateMachine.TransitionTo(SessionState.Attaching);
            throw;
        }


        if (stopAtEntry)
        {
            // SharpDbg does NOT implement stopAtEntry (no entry StoppedEvent is
            // sent after ConfigurationDone). Wait briefly in case a future
            // version adds the event, then return the HONEST state: if a stop
            // arrived → Stopped (OnStopped already transitioned); otherwise the
            // process is running. Never fabricate a stopped state.
            try
            {
                await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                _logger.LogInformation("Launch: stopped at entry.");
                ObserveStopState();
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
            catch (TimeoutException)
            {
                _logger.LogInformation("Launch: no entry StoppedEvent — returning with the process running.");
            }
        }
    }

    public async Task AttachAsync(int processId, CancellationToken ct = default)
    {
        // if (CurrentState != State.NotStarted)
        //     throw new InvalidOperationException("Session not in correct state for attach.");

        // New process lifecycle — drop any stop ledger state from a previous
        // process so its stops cannot surface in this session.
        ResetStopLedger();

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

        /// <summary>SharpDbg's breakpoint id — matches BreakpointEvent payloads.</summary>
        public int? AdapterId { get; set; }

        /// <summary>True when the adapter accepted the breakpoint but could not bind it yet (module not loaded).</summary>
        public bool IsPending { get; set; }

        // Writable so in-place line adjustments (response / BreakpointEvent)
        // keep object identity across _bpConfigs and _bpsByAdapterId.
        public int Line { get; set; } = Line;
        public int? EndLine { get; set; } = EndLine;
        public int? EndColumn { get; set; } = EndColumn;
    }

    private readonly Dictionary<(string File, int Line), BreakpointEntry> _bpConfigs = [];

    // Guards _bpConfigs: written by tool threads (breakpoint_set, inside the
    // session gate) and read by the DAP reader thread (OnStopped capture
    // resolution), which must never wait on the session gate (deadlock: a
    // waiting tool holds it). A short dedicated lock keeps both sides safe.
    private readonly object _bpConfigsLock = new();

    // Guards _captures: written by tool threads and by the capture
    // auto-continue task (deliberately outside the session gate).
    private readonly object _capturesLock = new();
    private readonly Dictionary<int, BreakpointEntry> _bpsByAdapterId = [];

    /// <summary>Normalized path → the FIRST path form used to set breakpoints in that file.
    /// SharpDbg keys breakpoint sets per source path string, so every re-send must use the
    /// same form — otherwise the adapter keeps parallel sets for the same file."</summary>
    private readonly Dictionary<string, string> _canonicalPaths = [];

    /// <summary>Modules reported by SharpDbg via ModuleEvent, keyed by module id (the module path).
    /// Populated from LoadModule callbacks — empty while the CLR is frozen (Attaching) and cleared on cleanup.</summary>
    private readonly Dictionary<string, LoadedModule> _modules = [];

    /// <summary>Module file name → whether SharpDbg loaded PDB symbols for it (parsed from
    /// SharpDbg's log lines). Used to attribute breakpoint bind failures.</summary>
    private readonly Dictionary<string, bool> _moduleSymbols = [];

    /// <summary>True when at least one loaded module has PDB symbols — distinguishes
    /// "no PDB anywhere" from "PDB exists but this path/line did not resolve".</summary>
    public bool HasAnySymbols
    {
        get
        {
            lock (_moduleSymbols)
                return _moduleSymbols.Values.Any(v => v);
        }
    }

    public IReadOnlyList<BreakpointEntry> SetBreakpoints(
        string filePath,
        params (int Line, int? Column, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth)[] breakpoints)
    {
        var normalizedFile = NormalizePath(filePath);
        // The FIRST path form wins for the adapter: re-sends must target the
        // same source-path key, otherwise SharpDbg keeps parallel breakpoint
        // sets for the same file (e.g. relative vs absolute, case differences).
        if (!_canonicalPaths.TryGetValue(normalizedFile, out var canonicalPath))
        {
            canonicalPath = filePath;
            _canonicalPaths[normalizedFile] = canonicalPath;
        }

        // Key the file registry by the NORMALIZED path so relative vs absolute
        // or differently-cased paths for the same file never produce two entries.
        _breakpointsByFile.Remove(normalizedFile);

        // Drop stale capture configs for this file — the set below replaces
        // all breakpoints in it, so old configs must not survive.
        lock (_bpConfigsLock)
        {
            foreach (var staleKey in _bpConfigs.Keys.Where(k => k.File == normalizedFile).ToList())
                _bpConfigs.Remove(staleKey);
        }

        var entries = new List<BreakpointEntry>();
        var sourceBreakpoints = new List<SourceBreakpoint>();

        foreach (var (line, col, cond, hitCond, action, captureScope, captureDepth) in breakpoints)
        {
            var entry = new BreakpointEntry(
                Id: _nextBreakpointId++,
                FilePath: canonicalPath,
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
            lock (_bpConfigsLock)
            {
                if (action == "capture")
                    _bpConfigs[configKey] = entry;
                else
                    _bpConfigs.Remove(configKey);
            }

            var sbp = new SourceBreakpoint { Line = line };
            if (col.HasValue) sbp.Column = col.Value;
            if (cond is not null) sbp.Condition = cond;
            if (hitCond is not null) sbp.HitCondition = hitCond;
            sourceBreakpoints.Add(sbp);
        }

        _breakpointsByFile[normalizedFile] = entries;

        var response = _host!.SendRequestSync(new SetBreakpointsRequest
        {
            Source = new Source { Path = canonicalPath },
            Breakpoints = sourceBreakpoints
        });

        var bpResults = response.Breakpoints;
        if (bpResults is not null)
        {
            for (int i = 0; i < Math.Min(entries.Count, bpResults.Count); i++)
            {
                entries[i].Verified = bpResults[i].Verified;
                entries[i].Message = bpResults[i].Message;
                entries[i].AdapterId = bpResults[i].Id;
                var bpLine = bpResults[i].Line;
                if (bpLine.HasValue && bpLine.Value != entries[i].Line)
                {
                    var oldKey = (normalizedFile, entries[i].Line);
                    entries[i].Line = bpLine.Value;
                    // Re-key the capture config so hit-location lookups still
                    // match when the adapter adjusts the line (e.g. moved to
                    // the next executable statement).
                    lock (_bpConfigsLock)
                    {
                        if (_bpConfigs.Remove(oldKey))
                            _bpConfigs[(normalizedFile, entries[i].Line)] = entries[i];
                    }
                }
            }
        }

        MarkPending(entries);
        RebuildAdapterIdMap();

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
                    entries[i].AdapterId = bpResults[i].Id;
                }
            }
        }

        MarkPending(entries);
        RebuildAdapterIdMap();

        return entries;
    }

    /// <summary>
    /// Derive the agent-facing status of a breakpoint:
    /// verified (bound), pending (accepted, module not loaded yet), or failed.
    /// </summary>
    public static string BreakpointStatus(BreakpointEntry entry)
        => entry.Verified ? "verified" : entry.IsPending ? "pending" : "failed";

    /// <summary>
    /// A breakpoint set before its module is loaded is reported unverified
    /// with SharpDbg's "not processed" message and binds later — mark it
    /// pending so callers can distinguish it from a genuine binding failure.
    /// </summary>
    private void MarkPending(IReadOnlyList<BreakpointEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.IsPending = !entry.Verified
                && (entry.Message == "Breakpoint has not been processed by the debugger."
                    || _stateMachine.Current == SessionState.Attaching);
        }
    }

    /// <summary>
    /// Rebuild the adapter-id → entry map. SharpDbg assigns a fresh id on
    /// every SetBreakpoints/SetFunctionBreakpoints call (including the
    /// re-send inside RemoveBreakpoint), so incremental maintenance would
    /// leave stale mappings — a full rebuild is simpler and always correct.
    /// </summary>
    private void RebuildAdapterIdMap()
    {
        _bpsByAdapterId.Clear();
        foreach (var entry in _breakpointsByFile.Values.SelectMany(v => v))
        {
            if (entry.AdapterId.HasValue)
                _bpsByAdapterId[entry.AdapterId.Value] = entry;
        }
        foreach (var entry in _functionBreakpoints)
        {
            if (entry.AdapterId.HasValue)
                _bpsByAdapterId[entry.AdapterId.Value] = entry;
        }
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
                // Re-send with the ORIGINAL path the breakpoints were set
                // with: SharpDbg keys breakpoint sets per source path, so a
                // normalized (lower-cased) path would be treated as a DIFFERENT
                // file and the old set (including the removed bp) would linger.
                var originalPath = entry.FilePath;
                if (entries.Count == 0)
                {
                    _breakpointsByFile.Remove(file);
                    _host!.SendRequestSync(new SetBreakpointsRequest
                    {
                        Source = new Source { Path = originalPath },
                        Breakpoints = new List<SourceBreakpoint>()
                    });
                }
                else
                {
                    SetBreakpoints(originalPath, entries.Select(e =>
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
        lock (_capturesLock)
            _captures.Add(snapshot);
        return snapshot;
    }

    public IReadOnlyList<CaptureSnapshot> GetCaptures()
    {
        // Snapshot copy: callers iterate outside the lock.
        lock (_capturesLock)
            return _captures.ToList();
    }

    public void ClearCaptures()
    {
        lock (_capturesLock)
        {
            _captures.Clear();
            _captureIndex = 0;
        }
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
    // Stop ledger — detects stops that occur while no tool call is waiting
    // ===================================================================

    /// <summary>
    /// True when a stop (breakpoint, exception, pause) occurred after the
    /// last time the client could have observed the session state — i.e.
    /// during the gap between a timed-out/cancelled wait and the next tool call.
    /// </summary>
    public bool HasUnobservedStop
        => Interlocked.Read(ref _stopSequence) > Interlocked.Read(ref _lastObservedSeq);

    /// <summary>
    /// Mark the current stop sequence as observed by the client. Called when a
    /// stop is delivered to the client (all delivery paths) and when
    /// state-revealing tools (debug_state, inspection) return.
    /// </summary>
    public void ObserveStopState()
        => Interlocked.Exchange(ref _lastObservedSeq, Interlocked.Read(ref _stopSequence));

    private void ResetStopLedger()
    {
        Interlocked.Exchange(ref _stopSequence, 0);
        Interlocked.Exchange(ref _lastObservedSeq, 0);
        // New process lifecycle: drop canonical path forms from the previous
        // process (the new process may live at a different path).
        _canonicalPaths.Clear();
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
        // Single linked CTS drives both timeout and cancellation so the delay
        // timer is always released when this method returns (no leaked
        // Task.Delay timers on long sessions). CancelAfter(timeout) only
        // arms when a positive timeout is given; otherwise the token just
        // follows user cancellation.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            waitCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);

        _logger.LogInformation("WaitForStop: waiting (timeout={Timeout}s)...", timeoutSeconds);

        var completed = await Task.WhenAny(stopTcs.Task, delayTask);

        // Tie-safe: a stop completing at the same instant as the timeout still
        // counts as a delivered stop — never report "running" when a stop
        // event is already available.
        if (completed == stopTcs.Task || stopTcs.Task.IsCompleted)
        {
            _logger.LogInformation("WaitForStop: stop event received");
            ObserveStopState();
            if (_stateMachine.Current == SessionState.Exited) return LastStop;
            return BuildStopEvent(stopTcs.Task.Result);
        }

        if (_stateMachine.Current == SessionState.Exited)
        {
            _logger.LogInformation("WaitForStop: process exited during wait");
            return LastStop;
        }

        if (ct.IsCancellationRequested)
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
        if (timeoutSeconds < 0)
            throw new ArgumentException("timeoutSeconds must be >= 0 (0 = no timeout).", nameof(timeoutSeconds));
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

        // The debuggee may have stopped while no tool call was waiting (e.g.
        // after a timed-out continue). Deliver the stop instead of resuming
        // past it — the client decides whether to resume with another call.
        if (HasUnobservedStop && _lastStop is { } missedStop)
        {
            ObserveStopState();
            _logger.LogInformation("Continue: delivering stop that occurred while not waiting (line={Line})",
                missedStop.Line);
            return missedStop with
            {
                Note = "The debuggee stopped while you were not waiting; it has NOT been resumed. " +
                       "Inspect with debug_state / stacktrace_get / variables_get, " +
                       "then call debug_continue again to resume."
            };
        }

        // Declare Running BEFORE sending the resume command: a stop can
        // arrive while the command is in flight (e.g. a breakpoint hit right
        // after resume), and OnStopped must never see the session as Attaching.
        var previousState = _stateMachine.Current;
        _stateMachine.TransitionTo(SessionState.Running);
        try
        {
            if (previousState == SessionState.Attaching)
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
        }
        catch
        {
            // Only roll back when no event has changed the state since — the
            // debuggee may already be running, stopped, or exited; those are
            // real states and must not be overwritten.
            if (_stateMachine.Current == SessionState.Running)
                _stateMachine.TransitionTo(previousState);
            throw;
        }

        // A stop may have arrived while the command was in flight (e.g. a
        // breakpoint hit immediately after resume). The TCS check is exact:
        // OnStopped resolves exactly the TCS swapped in above.
        if (stopTcs.Task.IsCompleted)
        {
            // The TCS may have been resolved by an exit (synthetic event) —
            // never overwrite the real Exited state.
            if (_stateMachine.Current != SessionState.Exited)
                _stateMachine.TransitionTo(SessionState.Stopped);
            return LastStop;
        }

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
        if (timeoutSeconds < 0)
            throw new ArgumentException("timeoutSeconds must be >= 0 (0 = no timeout).", nameof(timeoutSeconds));
        // Waiting is also valid when a stop already occurred while no tool
        // call was waiting — the client asked to wait for a stop, and one is
        // already there. Otherwise the debuggee must be running.
        if (_stateMachine.Current != SessionState.Running && !HasUnobservedStop)
            throw new InvalidOperationException($"Cannot wait: debugger state is {_stateMachine.Current}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        if (HasUnobservedStop && _lastStop is { } missedStop)
        {
            ObserveStopState();
            _logger.LogInformation("Wait: delivering stop that occurred while not waiting (line={Line})",
                missedStop.Line);
            return missedStop with
            {
                Note = "The debuggee stopped while you were not waiting; it has NOT been resumed. " +
                       "Call debug_continue to resume."
            };
        }

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

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        // The debuggee may have stopped while no tool call was waiting —
        // deliver the stop instead of stepping past it.
        if (HasUnobservedStop && _lastStop is { } missedStop)
        {
            ObserveStopState();
            _logger.LogInformation("Step: delivering stop that occurred while not waiting (line={Line})",
                missedStop.Line);
            return missedStop with
            {
                Note = "The debuggee stopped while you were not waiting; nothing was stepped. " +
                       "Call debug_step again to proceed, or debug_continue to resume."
            };
        }

        _stateMachine.TransitionTo(SessionState.Running);
        var tid = threadId ?? _lastStop?.ThreadId ?? 1;

        switch (type)
        {
            case "in": _host!.SendRequestSync(new StepInRequest(tid)); break;
            case "out": _host!.SendRequestSync(new StepOutRequest(tid)); break;
            default: _host!.SendRequestSync(new NextRequest(tid)); break;
        }

        StoppedEvent stopEvent;
        try
        {
            stopEvent = await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            // The step command was sent and the process resumed — a stop that
            // arrives later is caught by the stop ledger (gap delivery).
            throw new TimeoutException(
                "Step did not stop the process within 2s — the debuggee may be stuck. " +
                "Use debug_state to check, debug_wait to keep waiting, or debug_pause to interrupt.");
        }
        ObserveStopState();
        if (_stateMachine.Current == SessionState.Exited) return LastStop;
        return BuildStopEvent(stopEvent);
    }

    public async Task<StopEvent> PauseAsync(CancellationToken ct = default)
    {
        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {_stateMachine.Current}.");

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

        // The debuggee may have stopped on its own while no tool call was
        // waiting — pausing is then unnecessary; deliver the stop instead.
        if (HasUnobservedStop && _lastStop is { } missedStop)
        {
            ObserveStopState();
            _logger.LogInformation("Pause: delivering stop that occurred while not waiting (line={Line})",
                missedStop.Line);
            return missedStop with
            {
                Note = "The debuggee stopped on its own while you were not waiting; no pause was sent."
            };
        }

        // A breakpoint stop may have arrived before the pause was processed.
        if (stopTcs.Task.IsCompleted)
            return LastStop;

        _host!.SendRequestSync(new PauseRequest());

        StoppedEvent stopEvent;
        try
        {
            stopEvent = await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            // The process keeps running — a stop that arrives later is caught
            // by the stop ledger (gap delivery).
            throw new TimeoutException(
                "Pause did not stop the process within 2s — the debuggee may not respond to pause. " +
                "Use debug_state to check, or debug_wait to keep waiting.");
        }
        ObserveStopState();
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

    /// <summary>
    /// Upper bound for recursive variable expansion (variables_get / capture
    /// depth). Guards against request explosion on deep object graphs.
    /// </summary>
    public const int MaxExpandDepth = 10;

    public List<VariableInfo> GetVariablesForFrame(
        int frameId,
        string scope = "all",
        int depth = 0,
        IReadOnlySet<string>? expand = null)
    {
        if (depth < 0)
            throw new ArgumentException("depth must be >= 0.", nameof(depth));
        if (depth > MaxExpandDepth)
            throw new ArgumentException($"depth must be <= {MaxExpandDepth}.", nameof(depth));
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

        lock (_modules)
            _modules.Clear();

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

    /// <summary>
    /// Track modules as SharpDbg reports them (LoadModule callbacks). Only
    /// 'new' is emitted by SharpDbg; 'removed' is handled for completeness.
    /// Runs on the DAP reader thread; mutations are lock-protected because
    /// MCP tool threads read the list concurrently.
    /// </summary>
    private void OnModuleChanged(ModuleEvent e)
    {
        var m = e.Module;
        if (m is null || m.Id is not string id) return;

        if (e.Reason == ModuleEvent.ReasonValue.Removed)
        {
            lock (_modules)
                _modules.Remove(id);
            _logger.LogInformation("← ModuleEvent: removed {Name}", m.Name);
            return;
        }

        lock (_modules)
            _modules[id] = new LoadedModule(id, m.Name, m.Path);
        _logger.LogInformation("← ModuleEvent: {Name} ({Path})", m.Name, m.Path);
    }

    /// <summary>
    /// Snapshot of the modules reported so far. Empty until the program runs
    /// (first ConfigurationDone/continue) — the CLR is frozen before that, so
    /// SharpDbg has not received any LoadModule callbacks yet.
    /// </summary>
    public IReadOnlyList<LoadedModule> GetModules()
    {
        lock (_modules)
            return _modules.Values.ToList();
    }

    /// <summary>
    /// SharpDbg notifies when a previously pending breakpoint binds (module
    /// loaded): sync Verified/Message and the adjusted line, and re-key any
    /// capture config so hit-location lookups still match. Runs on the DAP
    /// reader thread; the mutations are plain reference writes read by MCP
    /// threads — the same model as _lastStop.
    /// </summary>
    private void OnBreakpointChanged(BreakpointEvent e)
    {
        var bp = e.Breakpoint;
        if (bp.Id is not { } adapterId
            || !_bpsByAdapterId.TryGetValue(adapterId, out var entry))
        {
            _logger.LogDebug("BreakpointEvent for unknown adapter breakpoint {Id} — ignoring", bp.Id);
            return;
        }

        entry.Verified = bp.Verified;
        entry.IsPending = false;
        // SharpDbg clears the message on successful bind — mirroring it drops
        // the stale "not been processed" text.
        entry.Message = bp.Message;

        if (bp.Line.HasValue && bp.Line.Value != entry.Line)
        {
            var oldKey = (NormalizePath(entry.FilePath), entry.Line);
            entry.Line = bp.Line.Value;
            if (bp.EndLine.HasValue) entry.EndLine = bp.EndLine;
            if (bp.EndColumn.HasValue) entry.EndColumn = bp.EndColumn;
            // Re-key the capture config: a capture breakpoint set early on a
            // non-executable line binds at an adjusted line — without this
            // the hit-location lookup misses and capture silently degrades
            // to a plain break.
            if (_bpConfigs.Remove(oldKey))
                _bpConfigs[(NormalizePath(entry.FilePath), entry.Line)] = entry;
        }

        _logger.LogInformation("← BreakpointEvent: id={Id} verified={Verified} line={Line}",
            adapterId, bp.Verified, bp.Line);
    }

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

        // Break-action or non-breakpoint stop: stay stopped, wake caller.
        // Bump the stop ledger — every non-capture stop counts (capture stops
        // return earlier and stay silent by design).
        Interlocked.Increment(ref _stopSequence);

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

        lock (_bpConfigsLock)
        {
            return _bpConfigs.TryGetValue((NormalizePath(file), line), out var cfg)
                && cfg.Action == "capture"
                ? new CaptureResolution(cfg.CaptureScope ?? "all", cfg.CaptureDepth)
                : null;
        }
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
        // Deliberately does NOT take the session gate: this runs while a
        // waiting tool call (debug_continue/debug_wait) holds the gate, and
        // taking it here would deadlock — the waiting call only releases the
        // gate after this capture's Continue makes progress. The race window
        // with concurrent tool calls is tiny (capture only starts when a stop
        // arrived with no waiting caller) and capture issues independent
        // DAP requests, so worst case a concurrent query gets an error it
        // can retry.
        _ = Task.Run(() =>
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
        });
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

        // Bound the buffered log so chatty debuggees cannot grow memory
        // without limit on long-running sessions. Trim in bulk (doubling
        // threshold) so bursts of output stay amortized O(1) per line.
        if (_outputLog.Count >= MaxOutputLogLines * 2)
            _outputLog.RemoveRange(0, _outputLog.Count - MaxOutputLogLines);
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

/// <summary>A module loaded into the debugged process, as reported by SharpDbg's LoadModule callback.</summary>
public record LoadedModule(string Id, string Name, string Path);

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
