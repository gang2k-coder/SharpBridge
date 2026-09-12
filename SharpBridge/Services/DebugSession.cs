using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
/// Architecture (session actor — S0 of the refactor, see
/// docs/superpowers/specs/2026-09-12-session-actor-refactor-spec-zh.md):
///   All DAP requests and session-state mutations are executed by ONE
///   dedicated consumer thread that reads a Channel&lt;SessionOp&gt; in FIFO
///   order. Producers (MCP tool threads, tests) enqueue an op and wait for
///   its completion — the wait happens OUTSIDE the queue, never on the
///   consumer (rule R1). DAP event handlers run on the host's reader thread
///   and must only enqueue (rule R3); the stop-generation bump stays on that
///   thread (rule R4).
///
///   Migration status (S3 — complete): every tool entry point, the capture
///   path and all DAP event handlers run on the consumer; SendDap enforces
///   that DAP requests originate there. The reader thread only enqueues (plus
///   the R4 stop-generation bump). Remaining cross-thread state, by design:
///   the stop-ledger publish/ack pair (HasUnobservedStop / ObserveStopState),
///   _stopGeneration (R4), and the two fields SharpDbg's log callback thread
///   writes — ProcessId (volatile-backed) and _moduleSymbols
///   (ConcurrentDictionary). There are no locks left in this file.
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

    // (S2) The former _sessionGate is gone: every DAP request and every
    // state mutation runs as an op on the session consumer, which serializes
    // them structurally. Waiting producers no longer block other tools — a
    // waiting debug_continue leaves the consumer free for pause/inspection.

    // (S1) The former _captureGate is gone: capture runs as a single op on the
    // session consumer, so two captures can never interleave DAP round-trips —
    // atomicity is structural now, not lock-based.

    // ===================================================================
    // Session actor (S0) — one consumer thread owns all DAP I/O and state
    // ===================================================================
    //
    // Rules that keep the model sound (see the design spec above):
    //   R1  ops never wait for events or for other ops — waits stay in the caller
    //   R2  capture runs as ONE op and only calls *Core methods (any enqueue
    //       from inside an op would self-deadlock on the head of the queue)
    //   R3  DAP event handlers only enqueue: they run on the reader thread,
    //       which also dispatches DAP responses — blocking it deadlocks
    //       everything, so the channel must stay unbounded
    //   R4  the stop-generation bump stays on the reader thread (it must be
    //       visible to a capture op that is already executing)
    //   R5  every enqueued op completes exactly once — result or exception
    //   R6  cancellation is explicit: pre-cancelled ops are never enqueued
    private readonly Channel<SessionOp> _ops = Channel.CreateUnbounded<SessionOp>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private System.Threading.Thread? _consumer;
    private int _consumerThreadId = -1;

    /// <summary>
    /// Set when the consumer loop has exited (channel completed and drained).
    /// From that point on session state is frozen — the reader thread was
    /// stopped and _host disposed by Cleanup — so state-only reads may run
    /// inline on the caller (see RunOnSessionState).
    /// </summary>
    private readonly ManualResetEventSlim _consumerStopped = new(false);

    /// <summary>
    /// Hard guard (S3, migration complete): every DAP request must originate on
    /// the session consumer thread. This used to be a soft warning per call
    /// site (the S1-S3 worklist); a violation is now a bug.
    /// </summary>
    private void GuardDapCaller(string caller)
    {
        if (Environment.CurrentManagedThreadId != _consumerThreadId)
            throw new InvalidOperationException(
                $"DAP request from '{caller}' must run on the session consumer thread.");
    }

    private sealed record SessionOp(string Name, Func<object?> Body, TaskCompletionSource<object?>? Completion);

    private void StartConsumer()
    {
        _consumer = new System.Threading.Thread(ConsumerLoop)
        {
            IsBackground = true,
            Name = "sharpbridge-session-consumer"
        };
        _consumer.Start();
    }

    private void ConsumerLoop()
    {
        _consumerThreadId = Environment.CurrentManagedThreadId;
        _logger.LogDebug("Session consumer thread started (id={ThreadId})", _consumerThreadId);

        while (true)
        {
            // Fast path: drain everything currently queued without touching
            // any async machinery. Then block until more work arrives or the
            // channel is completed and drained (WaitToReadAsync returns false).
            while (_ops.Reader.TryRead(out var op))
                RunOp(op);

            if (!_ops.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                break;
        }

        _consumerStopped.Set();
        _logger.LogDebug("Session consumer thread exiting (id={ThreadId})", _consumerThreadId);
    }

    private void RunOp(SessionOp op)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = op.Body();
            op.Completion?.TrySetResult(result);
        }
        catch (Exception ex)
        {
            if (op.Completion is null)
            {
                // Fire-and-forget op (capture): nobody is waiting, so an
                // escaping exception must not vanish silently (R5).
                _logger.LogError(ex, "Background session op '{Name}' failed", op.Name);
                return;
            }

            // Exceptions travel back to the producer through the TCS (R5) so
            // Filters.cs can surface the real message to the agent.
            op.Completion.TrySetException(ex);
        }
        finally
        {
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms > 1000)
                _logger.LogWarning("Session op '{Name}' took {ElapsedMs:F0} ms", op.Name, ms);
        }
    }

    /// <summary>
    /// Producer entry point: runs <paramref name="body"/> on the session
    /// consumer thread and returns its result. Callable from any thread
    /// EXCEPT the consumer itself — enqueueing from inside an op would
    /// self-deadlock (R2).
    /// </summary>
    public async Task<T> RunOnSessionAsync<T>(string name, Func<T> body, CancellationToken ct = default)
    {
        if (Environment.CurrentManagedThreadId == _consumerThreadId)
            throw new InvalidOperationException(
                $"Session op '{name}' cannot be enqueued from the session consumer thread (self-deadlock).");

        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ops.Writer.TryWrite(new SessionOp(name, () => body(), completion)))
            throw SessionNotActive();

        return (T)(await completion.Task.WaitAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Enqueue variant for ops that only READ session-owned state (breakpoint
    /// list, captures, modules). While the session is alive they behave like
    /// RunOnSession (ordering + no cross-thread reads). After the debuggee
    /// exited they keep working by reading the frozen state inline: Cleanup
    /// has already stopped the reader thread and disposed the host, and the
    /// consumer has exited, so nothing can mutate session state any more.
    /// The wait for the consumer is bounded — if it is stuck in a dead DAP
    /// call we fail fast instead of hanging.
    /// </summary>
    public T RunOnSessionState<T>(string name, Func<T> body)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_ops.Writer.TryWrite(new SessionOp(name, () => body(), completion)))
            return (T)completion.Task.GetAwaiter().GetResult()!;

        if (_consumer is null || !_consumerStopped.Wait(TimeSpan.FromSeconds(2)))
            throw SessionNotActive();

        return body();
    }

    /// <summary>Void variant of <see cref="RunOnSessionAsync{T}"/>: the op's
    /// body needs no result and only its exceptions matter.</summary>
    public async Task RunOnSessionAsync(string name, Action body, CancellationToken ct = default)
        => await RunOnSessionAsync(name, () => { body(); return true; }, ct).ConfigureAwait(false);

    /// <summary>
    /// Blocking variant for methods whose signatures must stay synchronous.
    /// Blocking the caller is equivalent to today's SendRequestSync behaviour:
    /// the caller already blocked on the very same DAP round trip.
    /// </summary>
    public T RunOnSession<T>(string name, Func<T> body)
        => RunOnSessionAsync(name, body).GetAwaiter().GetResult();

    /// <summary>Void variant of <see cref="RunOnSession{T}"/>.</summary>
    public void RunOnSession(string name, Action body)
        => RunOnSessionAsync(name, () => { body(); return true; }).GetAwaiter().GetResult();

    /// <summary>
    /// Fire-and-forget op for work triggered by the reader thread (R3: the
    /// reader must never block). The body is expected to handle its own
    /// exceptions; if one escapes, RunOp logs it — there is no producer to
    /// deliver it to.
    /// </summary>
    private void EnqueueBackground(string name, Action body)
    {
        if (!_ops.Writer.TryWrite(new SessionOp(name, () => { body(); return null; }, null)))
            _logger.LogDebug("Background op '{Name}' dropped: the session is closed.", name);
    }

    private TResponse SendDap<TArgs, TResponse>(
        DebugRequestWithResponse<TArgs, TResponse> request, [CallerMemberName] string? caller = null)
        where TArgs : class, new()
        where TResponse : ResponseBody
    {
        GuardDapCaller(caller ?? "?");
        EnsureActive();
        return _host!.SendRequestSync(request);
    }

    private void SendDap<TArgs>(DebugRequest<TArgs> request, [CallerMemberName] string? caller = null)
        where TArgs : class, new()
    {
        GuardDapCaller(caller ?? "?");
        EnsureActive();
        _host!.SendRequestSync(request);
    }

    private static InvalidOperationException SessionNotActive() =>
        new("The debug session is no longer active (process exited or disconnected). Start a new session.");

    private void EnsureActive()
    {
        if (_cleanedUp || _host is null)
            throw SessionNotActive();
    }

    // ===================================================================
    // StoppedEvent TCS — swapped before each async operation
    // ===================================================================
    // Consumer-owned since S3 (OnStopped runs there too), so plain swaps.
    private TaskCompletionSource<StoppedEvent> _pendingStopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ===================================================================
    // Stop ledger — detects stops that occur while no tool call is waiting
    // ===================================================================
    private long _stopSequence;
    private long _lastObservedSeq;

    /// <summary>
    /// Monotonic id of the most recent stop. Capture tasks record the id of
    /// the stop they were spawned for and refuse to resume/transition when a
    /// newer stop has superseded theirs — this is what keeps the state
    /// machine honest when capture auto-continue races with the next stop.
    /// </summary>
    private long _stopGeneration;

    /// <summary>
    /// Raw DAP stopped event of the most recent stop. Capture-failure delivery
    /// reuses it so the client receives the real stop instead of a fabricated
    /// one. Written on the reader thread (OnStopped), read by capture tasks —
    /// reference read, benign if stale.
    /// </summary>
    private StoppedEvent? _lastDapStop;

    // ===================================================================
    // Session state
    // ===================================================================
    public SessionState CurrentState => _stateMachine.Current;

    private StopEvent? _lastStop;
    private StopEvent LastStop => _lastStop
        ?? throw new InvalidOperationException("No stop event. Debugger may not be stopped.");
    private readonly Dictionary<string, List<BreakpointEntry>> _breakpointsByFile = new();
    private readonly List<BreakpointEntry> _functionBreakpoints = [];
    private int _nextBreakpointId = 1;
    private string? _adapterId;
    private List<ExceptionBreakpointsFilter>? _exceptionFilters;
    private int? _activeThreadId;
    private readonly List<CaptureSnapshot> _captures = [];
    private int _captureIndex;

    /// <summary>
    /// Cached breakpoint count, maintained on the session consumer by
    /// RecountBreakpoints(). Read directly (plain int) by GetBreakpointCount —
    /// tool threads must not enumerate the breakpoint dictionaries while the
    /// consumer mutates them, and execution-control decision paths must not
    /// pay a queue round-trip that widens their TOCTOU window.
    /// </summary>
    private int _breakpointCount;

    // ===================================================================
    // Session identity
    // ===================================================================
    // Written by two threads: the attach op (consumer) and SharpDbg's log
    // callback thread (launch PID discovery). Backed by an int so every write
    // and the cross-thread reads (manager, tools) are atomic and visible.
    private int _processId = -1;
    public int? ProcessId
    {
        get
        {
            var pid = Volatile.Read(ref _processId);
            return pid < 0 ? null : pid;
        }
        private set => Volatile.Write(ref _processId, value ?? -1);
    }
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
        _host.RegisterEventType<ContinuedEvent>(e =>
            _logger.LogInformation($"← ContinuedEvent: thread={e.ThreadId}"));
        _host.RegisterEventType<InitializedEvent>(e =>
            _logger.LogInformation("← InitializedEvent"));
        _host.RegisterEventType<ModuleEvent>(OnModuleChanged);

        _host.VerifySynchronousOperationAllowed();

        // Start the DAP message reader on a background thread
        _host.Run();

        // DAP handshake — privileged construction window: the consumer thread
        // does not exist yet and no other thread can reach this instance, so
        // this one call goes to the host directly instead of through SendDap.
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

        // From here on every DAP request and state mutation belongs to the
        // session consumer (S0).
        StartConsumer();
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
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RunOnSessionAsync("launch", () => LaunchCore(program, args, cwd, stopAtEntry, env, stopTcs), ct)
            .ConfigureAwait(false);

        if (!stopAtEntry) return;

        // SharpDbg 0.1.17+ implements stopAtEntry (an entry breakpoint at Main
        // delivers an Entry StoppedEvent after ConfigurationDone). The wait is
        // bounded and stays OUTSIDE the queue (R1) — the consumer must stay
        // free for other work. Return the HONEST state: if a stop arrived the
        // consumer marks Stopped; otherwise the process is running (older
        // adapters without stopAtEntry). Never fabricate a stopped state.
        try
        {
            await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            _logger.LogInformation("Launch: stopped at entry.");
            ObserveStopState();
            await RunOnSessionAsync("launch_entry_stop", MarkEntryStoppedCore, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Launch: no entry StoppedEvent — returning with the process running.");
        }
    }

    private void LaunchCore(
        string program,
        string[]? args,
        string? cwd,
        bool stopAtEntry,
        Dictionary<string, string>? env,
        TaskCompletionSource<StoppedEvent> stopTcs)
    {
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

        SendDap(new LaunchRequest
        {
            ConfigurationProperties = launchArgs
        });

        _stateMachine.TransitionTo(SessionState.Attaching);
        // Swap in a fresh stop TCS BEFORE configurationDone —
        // the StoppedEvent may fire as soon as the process starts.
        _pendingStopTcs = stopTcs;

        // Declare Running BEFORE ConfigurationDone: a stop (e.g. a breakpoint
        // hit right after resume) can arrive while the command is in flight,
        // and OnStopped must never see the session as Attaching.
        _stateMachine.TransitionTo(SessionState.Running);
        try
        {
            SendDap(new ConfigurationDoneRequest());
        }
        catch (Exception ex)
        {
            // A failed ConfigurationDone means the launch/attach did not
            // complete. Running→Attaching is not a valid state-machine
            // transition (it would throw and MASK this error), so do not
            // roll back — surface the real failure to the caller instead.
            _logger.LogError(ex, "ConfigurationDone failed (state={State})", _stateMachine.Current);
            throw new InvalidOperationException($"ConfigurationDone failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Consumer-side completion of the launch entry-stop handshake. The 2s
    /// wait ran on the caller (R1), so the state is re-checked HERE, atomically
    /// with the transition: the process may have exited, or a real stop may
    /// have arrived, while the caller was waiting.
    /// </summary>
    private void MarkEntryStoppedCore()
    {
        // The wait may have been resolved by the process exiting
        // (fast-exiting debuggees) — do not force Stopped in that case.
        if (_stateMachine.Current == SessionState.Exited)
        {
            _logger.LogInformation("Launch: process exited before the entry stop — leaving state as Exited.");
            return;
        }
        _stateMachine.TransitionTo(SessionState.Stopped);
    }

    public async Task AttachAsync(int processId, CancellationToken ct = default)
        => await RunOnSessionAsync("attach", () => AttachCore(processId), ct).ConfigureAwait(false);

    private void AttachCore(int processId)
    {
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
        SendDap(new AttachRequest
        {
            ConfigurationProperties = new Dictionary<string, JToken>
            {
                ["processId"] = processId
            }
        });
        _stateMachine.TransitionTo(SessionState.Attaching);
    }

    /// <summary>
    /// autoContinue after attach: complete the attach (ConfigurationDone +
    /// runtime resume) WITHOUT waiting for a stop, so capture-action
    /// breakpoints can fire silently from the very first debug_continue.
    /// A subsequent real breakpoint stop goes to the stop ledger (gap
    /// delivery with a "NOT been resumed" note) — same as any stop that
    /// occurs while no tool call is waiting.
    /// </summary>
    public async Task AttachAutoContinueAsync(CancellationToken ct = default)
    {
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RunOnSessionAsync("attach_auto_continue", () => AttachAutoContinueCore(stopTcs), ct)
            .ConfigureAwait(false);
    }

    private void AttachAutoContinueCore(TaskCompletionSource<StoppedEvent> stopTcs)
    {
        if (_stateMachine.Current != SessionState.Attaching)
            throw new InvalidOperationException($"Cannot auto-continue: debugger state is {_stateMachine.Current}.");

        // Swap in a fresh stop TCS BEFORE ConfigurationDone: any stop that
        // arrives after the runtime resume resolves THIS TCS and surfaces
        // through the normal ledger path instead of being lost.
        _pendingStopTcs = stopTcs;

        // Declare Running BEFORE the resume command (same pattern as
        // ContinueAndWaitAsync): a stop arriving while the command is in
        // flight transitions Running->Stopped on the reader thread.
        _stateMachine.TransitionTo(SessionState.Running);
        try
        {
            SendDap(new ConfigurationDoneRequest());
            if (ProcessId.HasValue)
            {
                try { DiagnosticClientHelper.DiagnosticClientResumeRuntime(ProcessId.Value).GetAwaiter().GetResult(); }
                catch (ServerNotAvailableException) { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "autoContinue resume failed (state={State})", _stateMachine.Current);
            throw new InvalidOperationException($"Auto-continue failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
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
        public string[]? CaptureExpressions { get; set; }
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

    // (S3) _bpConfigs is consumer-owned now: breakpoint_set/remove and the
    // BreakpointEvent / StoppedEvent handlers all run on the session consumer,
    // so its former lock is gone.

    private readonly Dictionary<int, BreakpointEntry> _bpsByAdapterId = [];

    /// <summary>Normalized path → the FIRST path form used to set breakpoints in that file.
    /// SharpDbg keys breakpoint sets per source path string, so every re-send must use the
    /// same form — otherwise the adapter keeps parallel sets for the same file."</summary>
    private readonly Dictionary<string, string> _canonicalPaths = [];

    /// <summary>Modules reported by SharpDbg via ModuleEvent, keyed by module id (the module path).
    /// Populated from LoadModule callbacks — empty while the CLR is frozen (Attaching) and cleared on cleanup.</summary>
    private readonly Dictionary<string, LoadedModule> _modules = [];

    /// <summary>Module file name → whether SharpDbg loaded PDB symbols for it (parsed from
    /// SharpDbg's log lines). Used to attribute breakpoint bind failures.
    /// Written by SharpDbg's log callback thread while tool threads read it via
    /// <see cref="HasAnySymbols"/>, so it must be a concurrent dictionary — a plain
    /// Dictionary throws (or reads torn state) when enumerated during a write.</summary>
    private readonly ConcurrentDictionary<string, bool> _moduleSymbols = new();

    /// <summary>True when at least one loaded module has PDB symbols — distinguishes
    /// "no PDB anywhere" from "PDB exists but this path/line did not resolve".</summary>
    public bool HasAnySymbols => _moduleSymbols.Values.Any(v => v);

    public IReadOnlyList<BreakpointEntry> SetBreakpoints(
        string filePath,
        params (int Line, int? Column, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth, string[]? CaptureExpressions)[] breakpoints)
        => RunOnSession("breakpoint_set", () => SetBreakpointsCore(filePath, breakpoints));

    private IReadOnlyList<BreakpointEntry> SetBreakpointsCore(
        string filePath,
        params (int Line, int? Column, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth, string[]? CaptureExpressions)[] breakpoints)
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
        foreach (var staleKey in _bpConfigs.Keys.Where(k => k.File == normalizedFile).ToList())
            _bpConfigs.Remove(staleKey);

        var entries = new List<BreakpointEntry>();
        var sourceBreakpoints = new List<SourceBreakpoint>();

        foreach (var (line, col, cond, hitCond, action, captureScope, captureDepth, captureExpressions) in breakpoints)
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
                CaptureDepth = captureDepth,
                CaptureExpressions = captureExpressions
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

        _breakpointsByFile[normalizedFile] = entries;

        var response = SendDap(new SetBreakpointsRequest
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
                    if (_bpConfigs.Remove(oldKey))
                        _bpConfigs[(normalizedFile, entries[i].Line)] = entries[i];
                }
            }
        }

        MarkPending(entries);
        RebuildAdapterIdMap();
        RecountBreakpoints();

        return entries;
    }

    /// <summary>
    /// Set function breakpoints. DAP SetFunctionBreakpoints REPLACES ALL function breakpoints.
    /// </summary>
    public IReadOnlyList<BreakpointEntry> SetFunctionBreakpoints(
        params (string Name, string? Condition, string? HitCondition,
                string Action, string? CaptureScope, int CaptureDepth)[] breakpoints)
        => RunOnSession("breakpoint_set_function", () => SetFunctionBreakpointsCore(breakpoints));

    private IReadOnlyList<BreakpointEntry> SetFunctionBreakpointsCore(
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
            var response = SendDap(new SetFunctionBreakpointsRequest
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
        RecountBreakpoints();

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
        => RunOnSession("breakpoint_remove", () => RemoveBreakpointCore(id));

    private bool RemoveBreakpointCore(int id)
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
                    SendDap(new SetBreakpointsRequest
                    {
                        Source = new Source { Path = originalPath },
                        Breakpoints = new List<SourceBreakpoint>()
                    });
                }
                else
                {
                    // Core call, NOT the public wrapper: enqueueing from inside
                    // an op would self-deadlock on the head of the queue (R2).
                    SetBreakpointsCore(originalPath, entries.Select(e =>
                        (e.Line, e.Column, e.Condition, e.HitCondition,
                         e.Action, e.CaptureScope, e.CaptureDepth, e.CaptureExpressions)).ToArray());
                }
                RecountBreakpoints();
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
            SendDap(new SetFunctionBreakpointsRequest { Breakpoints = remaining });
            RecountBreakpoints();
            return true;
        }

        return false;
    }

    public IReadOnlyList<BreakpointEntry> GetAllBreakpoints()
        => RunOnSessionState("breakpoint_list", GetAllBreakpointsCore);

    private IReadOnlyList<BreakpointEntry> GetAllBreakpointsCore()
        => _breakpointsByFile.Values.SelectMany(v => v)
            .Concat(_functionBreakpoints)
            .OrderBy(e => e.Id).ToList();

    public int GetBreakpointCount() => _breakpointCount;

    private int BreakpointCountCore
        => _breakpointsByFile.Values.Sum(v => v.Count) + _functionBreakpoints.Count;

    /// <summary>Refresh <see cref="_breakpointCount"/>; call from every op that mutates breakpoints.</summary>
    private void RecountBreakpoints() => _breakpointCount = BreakpointCountCore;

    // ===================================================================
    // Capture System
    // ===================================================================

    public CaptureSnapshot CaptureState(
        string scope = "all", int depth = 0, int? breakpointId = null,
        IReadOnlyList<CapturedExpression>? expressions = null)
        => RunOnSession("capture_state", () => CaptureStateCore(scope, depth, breakpointId, expressions));

    /// <summary>
    /// Runs on the session consumer. Manual (tool) captures and auto-captures
    /// are both ops now, so they can never interleave their DAP round-trips.
    /// </summary>
    private CaptureSnapshot CaptureStateCore(
        string scope, int depth, int? breakpointId,
        IReadOnlyList<CapturedExpression>? expressions)
    {
        EnsureStopped();

        // Location comes from the actual top frame — the ground truth for
        // "where the snapshot was taken", and it works even when no stop
        // event was ever processed (e.g. launch with pause-success).
        var frame = GetStackTraceCore(_activeThreadId ?? 1, 0, 1).FirstOrDefault();

        var snapshot = new CaptureSnapshot(
            Index: ++_captureIndex,
            Reason: _lastStop?.Reason,
            ThreadId: _lastStop?.ThreadId,
            FilePath: frame?.Source,
            Line: frame?.Line ?? 0,
            Variables: frame is null ? [] : GetVariablesForFrameCore(frame.Id, scope, depth, expand: null),
            Timestamp: DateTime.UtcNow,
            BreakpointId: breakpointId,
            Expressions: expressions is { Count: > 0 } ? expressions : null);
        _captures.Add(snapshot);
        return snapshot;
    }

    public IReadOnlyList<CaptureSnapshot> GetCaptures()
        => RunOnSessionState("capture_list", () => _captures.ToList().AsReadOnly());

    public void ClearCaptures()
        => RunOnSession("capture_clear", () =>
        {
            _captures.Clear();
            _captureIndex = 0;
        });

    // ===================================================================
    // Exception Breakpoints
    // ===================================================================

    public IReadOnlyList<ExceptionBreakpointsFilter>? GetExceptionBreakpointFilters()
        => _exceptionFilters;

    public void SetExceptionBreakpoints(string[] filters)
        => RunOnSession("exception_breakpoints_set", () =>
        {
            SendDap(new SetExceptionBreakpointsRequest
            {
                Filters = filters.ToList()
            });
            _logger.LogInformation($"Exception breakpoints set: [{string.Join(", ", filters)}]");
        });

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
        Interlocked.Exchange(ref _stopGeneration, 0);
        _lastDapStop = null;
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
            var built = BuildStopEvent(stopTcs.Task.Result);
            // Capture-failure delivery sets a note on _lastStop (the raw DAP
            // event cannot carry one) — surface it to the waiting caller.
            if (built.Note is null && _lastStop?.Note is { } note)
                built = built with { Note = note };
            return built;
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
        if (_breakpointCount == 0 && timeoutSeconds == 0)
        {
            throw new InvalidOperationException(
                "No breakpoints set and timeout is disabled (0 = infinite). " +
                "Set a breakpoint with breakpoint_set first, " +
                "or specify a timeout value (e.g. timeout=30).");
        }

        // Swap the TCS and take the resume/wait decision in ONE op — the old
        // two-method split (read state here, re-check in WaitAndWaitAsync)
        // was a TOCTOU that OnStopped could invalidate in between. The wait
        // stays outside the queue (R1).
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = await RunOnSessionAsync("continue", () => ContinueCore(stopTcs), ct).ConfigureAwait(false);
        if (delivered is not null) return delivered;

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct, stopTcs).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the session consumer. Returns a stop to deliver immediately
    /// (missed stop / stop that arrived while the resume was in flight), or
    /// null when the caller should wait on <paramref name="stopTcs"/>.
    /// </summary>
    private StopEvent? ContinueCore(TaskCompletionSource<StoppedEvent> stopTcs)
    {
        // Swap in a fresh stop TCS BEFORE any decision: any stop that arrives
        // after this point resolves THIS TCS, so none can be missed.
        _pendingStopTcs = stopTcs;

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

        // Right after debug_launch with stopAtEntry=false, the debuggee is
        // already running and launch returns honestly with state Running.
        // "Continue" then means "wait for the next stop" — resuming a running
        // process is superfluous and SharpDbg would reject the request. This
        // decision is now atomic with the TCS swap above.
        if (_stateMachine.Current == SessionState.Running)
            return null;

        if (_stateMachine.Current != SessionState.Stopped && _stateMachine.Current != SessionState.Attaching)
            throw new InvalidOperationException($"Cannot continue: debugger state is {_stateMachine.Current}.");

        // Declare Running BEFORE sending the resume command: a stop can
        // arrive while the command is in flight (e.g. a breakpoint hit right
        // after resume), and OnStopped must never see the session as Attaching.
        var previousState = _stateMachine.Current;
        _stateMachine.TransitionTo(SessionState.Running);
        try
        {
            if (previousState == SessionState.Attaching)
            {
                SendDap(new ConfigurationDoneRequest());
                if (ProcessId.HasValue)
                {
                    try { DiagnosticClientHelper.DiagnosticClientResumeRuntime(ProcessId.Value).GetAwaiter().GetResult(); }
                    catch (ServerNotAvailableException) { }
                }
            }
            else
            {
                SendDap(new ContinueRequest { ThreadId = _lastStop?.ThreadId ?? 0 });
            }
        }
        catch (Exception ex)
        {
            // A failed resume command. Rolling back is only valid when the
            // prior state was Stopped (Running→Stopped is legal). For an
            // Attaching resume, Running→Attaching is NOT a legal transition
            // and the old rollback threw, MASKING the real failure — surface
            // the original error instead.
            if (_stateMachine.Current == SessionState.Running && previousState == SessionState.Stopped)
                _stateMachine.TransitionTo(previousState);
            _logger.LogError(ex, "Resume command failed (state={State}, previous={Previous})",
                _stateMachine.Current, previousState);
            throw new InvalidOperationException($"Resume failed: {ex.GetType().Name}: {ex.Message}", ex);
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

        return null;
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

        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = await RunOnSessionAsync("wait", () => WaitCore(stopTcs), ct).ConfigureAwait(false);
        if (delivered is not null) return delivered;

        return await WaitForStopInTimespanAsync(timeoutSeconds, ct, stopTcs).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the session consumer: installs the TCS and validates that the
    /// session is in a waitable state, atomically with respect to other ops.
    /// </summary>
    private StopEvent? WaitCore(TaskCompletionSource<StoppedEvent> stopTcs)
    {
        _pendingStopTcs = stopTcs;

        // Waiting is also valid when a stop already occurred while no tool
        // call was waiting — the client asked to wait for a stop, and one is
        // already there. Otherwise the debuggee must be running.
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

        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot wait: debugger state is {_stateMachine.Current}.");

        // A stop may have arrived between the state guard and the swap —
        // report it instead of waiting for the next stop.
        if (stopTcs.Task.IsCompleted)
            return LastStop;

        return null;
    }

    public async Task<StopEvent> StepAsync(
        string type, int? threadId = null, CancellationToken ct = default)
    {
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = await RunOnSessionAsync("step", () => StepCore(type, threadId, stopTcs), ct)
            .ConfigureAwait(false);
        if (delivered is not null) return delivered;

        StoppedEvent stopEvent;
        try
        {
            stopEvent = await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Runs on the session consumer: installs the TCS, delivers a missed stop,
    /// or sends the step request — all atomic with respect to other ops.
    /// </summary>
    private StopEvent? StepCore(string type, int? threadId, TaskCompletionSource<StoppedEvent> stopTcs)
    {
        if (_stateMachine.Current != SessionState.Stopped)
            throw new InvalidOperationException($"Cannot step: debugger state is {_stateMachine.Current}.");

        _pendingStopTcs = stopTcs;

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
            case "in": SendDap(new StepInRequest(tid)); break;
            case "out": SendDap(new StepOutRequest(tid)); break;
            default: SendDap(new NextRequest(tid)); break;
        }

        return null;
    }

    public async Task<StopEvent> PauseAsync(CancellationToken ct = default)
    {
        var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        StopEvent? delivered;
        try
        {
            delivered = await RunOnSessionAsync("pause", () => PauseCore(stopTcs), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAlreadyStoppedError(ex))
        {
            return await RecoverStoppedTruthAsync(ex, ct).ConfigureAwait(false);
        }
        if (delivered is not null) return delivered;

        try
        {
            return await WaitForPauseStopAsync(stopTcs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A pause that produces no event may mean the debuggee was already
            // stopped: SharpDbg treats Pause on a stopped process as a no-op
            // and emits no StoppedEvent (seen in capture auto-continue loops).
            // Retry once — a rejection proves it is stopped (recover the
            // truth), a success stops the process for real.
            var retryTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                delivered = await RunOnSessionAsync("pause_retry", () => PauseCore(retryTcs), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || IsAlreadyStoppedError(ex))
            {
                // Rejected because it is already stopped, or the state machine
                // no longer claims Running — both mean the client must get a
                // truthful stop instead of a wedged session.
                return await RecoverStoppedTruthAsync(ex, ct).ConfigureAwait(false);
            }
            if (delivered is not null) return delivered;

            try
            {
                return await WaitForPauseStopAsync(retryTcs, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The process keeps running — a stop that arrives later is
                // caught by the stop ledger (gap delivery).
                throw new TimeoutException(
                    "Pause did not stop the process within 2s — the debuggee may not respond to pause. " +
                    "Use debug_state to check, or debug_wait to keep waiting.");
            }
        }
    }

    private async Task<StopEvent> WaitForPauseStopAsync(
        TaskCompletionSource<StoppedEvent> stopTcs, CancellationToken ct)
    {
        var stopEvent = await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        ObserveStopState();
        if (_stateMachine.Current == SessionState.Exited) return LastStop;
        return BuildStopEvent(stopEvent);
    }

    /// <summary>SharpDbg's "the process is not running" rejection text.</summary>
    private static bool IsAlreadyStoppedError(Exception ex)
        => ex.Message.Contains("not running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The adapter says the process is already stopped while our state machine
    /// says Running — the state machine is the one that is wrong (a stop was
    /// consumed without a StoppedEvent reaching us, e.g. during a capture
    /// auto-continue loop). Re-establish the truth so the client can inspect and
    /// resume instead of being wedged on a session that cannot be paused and
    /// never reports a stop.
    /// </summary>
    private async Task<StopEvent> RecoverStoppedTruthAsync(Exception cause, CancellationToken ct)
    {
        var state = await RunOnSessionAsync("recover_stopped", () =>
        {
            if (_stateMachine.Current == SessionState.Running)
                _stateMachine.TransitionTo(SessionState.Stopped);
            return _stateMachine.Current;
        }, ct).ConfigureAwait(false);

        if (state is SessionState.Exited or SessionState.Detached)
            return _lastStop ?? new StopEvent("exited", null, null, "exited", null, 0, 0);

        _logger.LogWarning(cause,
            "Pause was rejected because the debuggee is already stopped; re-established the Stopped " +
            "state so the client is not wedged on a Running session with nothing to deliver.");

        return new StopEvent("stopped", null, null, "pause", null, 0, 0)
        {
            Note = "The debuggee is already stopped (the adapter rejected the pause). " +
                   "Use stacktrace_get / variables_get to inspect, then debug_continue to resume."
        };
    }

    /// <summary>
    /// Runs on the session consumer: installs the TCS, delivers a stop that
    /// happened on its own, or sends the pause request.
    /// </summary>
    private StopEvent? PauseCore(TaskCompletionSource<StoppedEvent> stopTcs)
    {
        if (_stateMachine.Current != SessionState.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {_stateMachine.Current}.");

        _pendingStopTcs = stopTcs;

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

        SendDap(new PauseRequest());
        return null;
    }

    // ===================================================================
    // Inspection
    // ===================================================================

    /// <summary>
    /// Migrated to the session consumer (S0 reference implementation): the
    /// public method enqueues and blocks, the Core method runs on the consumer.
    /// </summary>
    public List<ThreadInfo> GetThreads()
        => RunOnSession("threads_list", GetThreadsCore);

    private List<ThreadInfo> GetThreadsCore()
    {
        EnsureStopped();
        var response = SendDap(new ThreadsRequest());
        return response.Threads.Select(t => new ThreadInfo(
            t.Id, t.Name, t.Id == _activeThreadId)).ToList();
    }

    public List<StackFrameInfo> GetStackTrace(int threadId, int startFrame = 0, int? levels = null)
        => RunOnSession("stacktrace_get", () => GetStackTraceCore(threadId, startFrame, levels));

    private List<StackFrameInfo> GetStackTraceCore(int threadId, int startFrame, int? levels)
    {
        EnsureStopped();
        var response = SendDap(new StackTraceRequest
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
        => RunOnSession("variables_get", () => GetVariablesForFrameCore(frameId, scope, depth, expand));

    private List<VariableInfo> GetVariablesForFrameCore(
        int frameId, string scope, int depth, IReadOnlySet<string>? expand)
    {
        if (depth < 0)
            throw new ArgumentException("depth must be >= 0.", nameof(depth));
        if (depth > MaxExpandDepth)
            throw new ArgumentException($"depth must be <= {MaxExpandDepth}.", nameof(depth));
        EnsureStopped();
        var scopes = GetScopesCore(frameId);
        if (scopes.Count == 0)
            throw new InvalidOperationException(
                $"Frame {frameId} not found in the current stack. " +
                "Re-fetch the stack with stacktrace_get after any step or continue.");

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
            var vars = ExpandVariablesCore(s.VariablesReference);
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
        var children = ExpandVariablesCore(variablesReference);
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
        => RunOnSession("variables_expand", () => ExpandVariablesCore(variablesReference));

    private List<VariableInfo> ExpandVariablesCore(int variablesReference)
    {
        EnsureStopped();
        var response = SendDap(new VariablesRequest
        {
            VariablesReference = variablesReference
        });

        return response.Variables.Select(v => new VariableInfo(
            v.Name, v.Value, v.Type, v.VariablesReference,
            v.EvaluateName, v.IndexedVariables, v.NamedVariables)).ToList();
    }

    private List<ScopeInfo> GetScopesCore(int frameId)
    {
        var response = SendDap(new ScopesRequest { FrameId = frameId });
        return response.Scopes.Select(s => new ScopeInfo(s.Name, s.VariablesReference, s.Expensive)).ToList();
    }

    public async Task<EvalResult> EvaluateAsync(string expression, int? frameId = null)
        => await RunOnSessionAsync("evaluate", () => EvaluateCore(expression, frameId)).ConfigureAwait(false);

    private EvalResult EvaluateCore(string expression, int? frameId)
    {
        EnsureStopped();
        var response = SendDap(new EvaluateRequest
        {
            Expression = expression,
            FrameId = frameId,
            Context = EvaluateArguments.ContextValue.Repl
        });
        var isError = response.PresentationHint?.Attributes?.HasFlag(
            VariablePresentationHint.AttributesValue.FailedEvaluation) == true;
        return new EvalResult(response.Result, response.Type, response.VariablesReference, isError);
    }

    public ExceptionDetail? GetExceptionInfo(int? threadId = null)
        => RunOnSession("exception_info", () => GetExceptionInfoCore(threadId));

    private ExceptionDetail? GetExceptionInfoCore(int? threadId)
    {
        EnsureStopped();
        try
        {
            var response = SendDap(new ExceptionInfoRequest
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
        // Already closed (process exited or a previous disconnect) — nothing
        // to do, matching the old no-op behaviour.
        if (_cleanedUp) return;

        try
        {
            // On the consumer (since S2) so the DisconnectRequest cannot
            // interleave with another op; Cleanup then runs there too.
            RunOnSession("disconnect", () => DisconnectCore(terminateDebuggee));
        }
        catch (InvalidOperationException) when (_cleanedUp)
        {
            // The session closed underneath us — nothing left to disconnect.
        }
    }

    private void DisconnectCore(bool terminateDebuggee)
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
            SendDap(new DisconnectRequest { TerminateDebuggee = terminateDebuggee });
        }
        catch { }
        Cleanup();
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        // (S1) _modules is consumer-owned and unreachable once _host is null
        // (every entry point fails fast through EnsureActive), so it needs no
        // Clear() and no lock here.

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

        // Stop the session consumer. Ops still queued are drained and fail fast
        // with "session no longer active" (EnsureActive sees _host == null);
        // ops enqueued after this point are rejected by TryWrite (R5). Never
        // block here waiting for the consumer to exit (Q4).
        _ops.Writer.TryComplete();

        if (ProcessId.HasValue)
            _onDisposed?.Invoke(ProcessId.Value);
    }

    // ===================================================================
    // Event handlers (on host's internal reader thread)
    // ===================================================================

    /// <summary>
    /// Track modules as SharpDbg reports them (LoadModule callbacks). Only
    /// 'new' is emitted by SharpDbg; 'removed' is handled for completeness.
    /// Runs on the DAP reader thread: enqueue only (R3). _modules is now
    /// consumer-owned, so no lock is needed.
    /// </summary>
    private void OnModuleChanged(ModuleEvent e)
        => EnqueueBackground("evt:module", () => ApplyModuleChangeCore(e));

    private void ApplyModuleChangeCore(ModuleEvent e)
    {
        var m = e.Module;
        if (m is null || m.Id is not string id) return;

        if (e.Reason == ModuleEvent.ReasonValue.Removed)
        {
            _modules.Remove(id);
            _logger.LogInformation("← ModuleEvent: removed {Name}", m.Name);
            return;
        }

        _modules[id] = new LoadedModule(id, m.Name, m.Path);
        _logger.LogInformation("← ModuleEvent: {Name} ({Path})", m.Name, m.Path);
    }

    /// <summary>
    /// Snapshot of the modules reported so far. Empty until the program runs
    /// (first ConfigurationDone/continue) — the CLR is frozen before that, so
    /// SharpDbg has not received any LoadModule callbacks yet.
    /// </summary>
    public IReadOnlyList<LoadedModule> GetModules()
        => RunOnSessionState("modules_list", () => _modules.Values.ToList());

    /// <summary>
    /// SharpDbg notifies when a previously pending breakpoint binds (module
    /// loaded): sync Verified/Message and the adjusted line, and re-key any
    /// capture config so hit-location lookups still match. Runs on the DAP
    /// reader thread: enqueue only (R3) — the mutations happen on the consumer,
    /// ordered with the stop handling that reads them (single FIFO queue).
    /// </summary>
    private void OnBreakpointChanged(BreakpointEvent e)
        => EnqueueBackground("evt:breakpoint", () => ApplyBreakpointChangeCore(e));

    private void ApplyBreakpointChangeCore(BreakpointEvent e)
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
        // Bump the stop generation FIRST, on the reader thread (R4): a capture
        // that is already executing on the consumer compares against it to
        // detect that a newer stop superseded the one it serves.
        var generation = Interlocked.Increment(ref _stopGeneration);

        // Everything else belongs to the consumer (R3): the reader thread also
        // dispatches DAP responses and must never block or issue DAP I/O.
        EnqueueBackground("evt:stopped", () => HandleStoppedCore(e, generation));
    }

    /// <summary>
    /// Runs on the session consumer: records the stop, decides whether it is a
    /// capture stop (auto-capture + resume happen INLINE so the whole sequence
    /// is atomic with this decision), or delivers it to a waiting caller via
    /// the ledger + pending TCS.
    /// </summary>
    private void HandleStoppedCore(StoppedEvent e, long generation)
    {
        _logger.LogInformation("→ OnStopped: reason={Reason}, thread={ThreadId}, state={State}",
            e.Reason, e.ThreadId, _stateMachine.Current);

        _activeThreadId = e.ThreadId;
        _stateMachine.TransitionTo(SessionState.Stopped);

        // Record the stop before any branching: capture snapshots, waiters and
        // step/pause re-checks all rely on _lastStop being set for every stop.
        _lastDapStop = e;
        _lastStop = BuildStopEvent(e);

        // Capture-action breakpoints auto-capture and continue without waking
        // the caller. Resolution needs no DAP requests — SharpDbg delivers the
        // hit location in the event itself. The capture runs INLINE because we
        // are already on the consumer: it is atomic with this stop decision.
        if (e.Reason == StoppedEvent.ReasonValue.Breakpoint && _bpConfigs.Count > 0)
        {
            if (TryResolveCapture(e) is { } capture)
            {
                // DO NOT touch _pendingStopTcs — the caller keeps waiting and
                // the next stop (or exit) resolves it.
                _logger.LogInformation("← OnStopped: auto-continue (capture), TCS not touched, thread={ThreadId}", e.ThreadId);
                RunCaptureAndContinueCore(
                    e.ThreadId, generation, capture.Scope, capture.Depth, capture.BreakpointId, capture.Expressions);
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
        var old = _pendingStopTcs;
        _pendingStopTcs = newTcs;
        old.TrySetResult(e);
        _logger.LogInformation("← OnStopped: TCS resolved (reason={Reason}, thread={ThreadId}), new TCS created",
            e.Reason, e.ThreadId);
    }

    /// <summary>
    /// Resolve whether a breakpoint stop is a capture-action breakpoint, using
    /// only the hit location SharpDbg embeds in the stopped event. Pure
    /// in-memory lookup on the consumer thread (S3: _bpConfigs is consumer-
    /// owned, so no lock is needed). Returns null when the event carries no
    /// location or no config matches.
    /// </summary>
    private CaptureResolution? TryResolveCapture(StoppedEvent e)
    {
        if (!TryGetHitLocation(e, out var file, out var line))
            return null;

        return _bpConfigs.TryGetValue((NormalizePath(file), line), out var cfg)
            && cfg.Action == "capture"
            ? new CaptureResolution(
                cfg.CaptureScope ?? "all", cfg.CaptureDepth, cfg.Id, cfg.CaptureExpressions ?? [])
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

    private readonly record struct CaptureResolution(string Scope, int Depth, int BreakpointId, string[] Expressions);

    /// <summary>
    /// Runs as a single background op on the session consumer (S1): the whole
    /// capture — captureExpressions, state snapshot, auto-continue — is atomic
    /// against every other session op, which is what the old _captureGate
    /// approximated. The generation guard re-checks the stop this op serves
    /// before resuming: a newer stop owns the resume decision (R4).
    /// </summary>
    private void RunCaptureAndContinueCore(
        int? threadId, long stopGeneration, string scope, int depth, int breakpointId, string[] expressions)
    {
        try
        {
            // captureExpressions: evaluated at hit time while the frame is
            // still alive — before CaptureState, before the auto-continue
            // resume. Each failure is recorded per-expression and never
            // fails the capture (mirrors the conditional-breakpoint
            // skip-on-error semantics).
            var expressionResults = new List<CapturedExpression>();
            if (expressions.Length > 0)
            {
                var frame = GetStackTraceCore(_activeThreadId ?? 1, 0, 1).FirstOrDefault();
                foreach (var expr in expressions)
                {
                    if (frame is null)
                    {
                        expressionResults.Add(new CapturedExpression(expr, null, true));
                        continue;
                    }
                    try
                    {
                        var r = EvaluateCore(expr, frame.Id);
                        expressionResults.Add(new CapturedExpression(
                            expr, r.IsError ? null : r.Result, r.IsError));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "captureExpressions: evaluation failed for '{Expression}'", expr);
                        expressionResults.Add(new CapturedExpression(expr, null, true));
                    }
                }
            }

            CaptureStateCore(scope, depth, breakpointId, expressionResults);

            if (_host is null || _stateMachine.Current is SessionState.Exited or SessionState.Detached)
            {
                _logger.LogWarning("Capture recorded but the session is no longer active — skipping auto-continue.");
                return;
            }

            // A newer stop superseded this one while we were capturing —
            // do NOT resume: that stop's own capture op owns the resume
            // decision. Resuming here would race (and corrupt) the state
            // machine, and the newest stop is frozen awaiting its capture.
            if (Interlocked.Read(ref _stopGeneration) != stopGeneration)
            {
                _logger.LogInformation(
                    "Capture auto-continue skipped: a newer stop superseded this one (gen {Served} -> {Current}).",
                    stopGeneration, Interlocked.Read(ref _stopGeneration));
                return;
            }

            // Declare Running BEFORE sending the resume command (same
            // pattern as ContinueAndWaitAsync): a stop that arrives while the
            // request is in flight transitions Running->Stopped on the reader
            // thread and is never overwritten by this op.
            _stateMachine.TransitionTo(SessionState.Running);
            SendDap(new ContinueRequest { ThreadId = threadId ?? 0 });
        }
        catch (Exception ex)
        {
            CaptureFailed(ex, stopGeneration);
        }
    }

    /// <summary>
    /// Auto-continue did not happen (capture error, superseded-stop race,
    /// adapter error) while the debuggee is still paused at the breakpoint.
    /// Deliver the truth to any waiting caller instead of letting the wait
    /// time out into a lying "running": bump the ledger, resolve the pending
    /// stop TCS with the real stop event, and keep the state Stopped.
    /// </summary>
    private void CaptureFailed(Exception ex, long stopGeneration)
    {
        _logger.LogError(ex,
            "Capture auto-continue failed; delivering the stop to the client instead of leaving the debuggee silently paused.");

        // A newer stop already owns the delivery (OnStopped bumped the ledger
        // and resolved the TCS) — do not double-deliver.
        if (Interlocked.Read(ref _stopGeneration) != stopGeneration)
        {
            _logger.LogWarning("Capture failure superseded by a newer stop; the newer stop's delivery path owns the session.");
            return;
        }

        // Exit/disconnect already owns the state and TCS — do not override.
        if (_stateMachine.Current is SessionState.Exited or SessionState.Detached)
            return;

        // Running->Stopped (we declared Running before a failed send) and
        // Stopped->Stopped are both valid — re-establish the truth.
        _stateMachine.TransitionTo(SessionState.Stopped);

        var note = "Capture auto-continue failed: " + ex.Message +
                   " The debuggee is STOPPED at the breakpoint — inspect it, then call debug_continue to resume.";
        _lastStop = _lastStop is { } last
            ? last with { Note = note }
            : new StopEvent("stopped", _lastDapStop?.ThreadId, _lastDapStop?.AllThreadsStopped,
                "breakpoint", null, 0, 0) { Note = note };

        // Same delivery order as OnStopped: bump the ledger BEFORE resolving
        // the TCS, so the waking caller observes the sequence and the next
        // debug_continue resumes normally instead of re-delivering the stop.
        Interlocked.Increment(ref _stopSequence);
        var newTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = _pendingStopTcs;
        _pendingStopTcs = newTcs;
        old.TrySetResult(_lastDapStop ?? new StoppedEvent(reason: StoppedEvent.ReasonValue.Breakpoint));
        _logger.LogInformation("Capture failure delivered as a stop (reason=breakpoint, state={State})",
            _stateMachine.Current);
    }

    /// <summary>
    /// Process exit runs on the consumer (R3): the reader thread only
    /// enqueues. Cleanup then runs there too, which keeps it ordered behind
    /// every op that was already queued.
    /// </summary>
    private void OnExited(ExitedEvent e)
        => EnqueueBackground("evt:exited", () => HandleExitedCore(e));

    private void HandleExitedCore(ExitedEvent e)
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
        => EnqueueBackground("evt:terminated", HandleTerminatedCore);

    private void HandleTerminatedCore()
    {
        _stateMachine.TransitionTo(SessionState.Exited);
        CompletePendingStopTcs();
        _logger.LogInformation("← TerminatedEvent");
        Cleanup();
    }

    private void CompletePendingStopTcs()
    {
        var newTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = _pendingStopTcs;
        _pendingStopTcs = newTcs;
        old.TrySetResult(
            new StoppedEvent(reason: StoppedEvent.ReasonValue.Breakpoint));
        _logger.LogInformation("CompletePendingStopTcs: TCS resolved (synthetic Breakpoint), state={State}",
            _stateMachine.Current);
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
            e.Reason.ToString().ToLowerInvariant(),
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
public record EvalResult(string Result, string? Type, int VariablesReference, bool IsError = false);
public record CapturedExpression(string Expression, string? Value, bool IsError);
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
    DateTime Timestamp,
    int? BreakpointId,
    IReadOnlyList<CapturedExpression>? Expressions);
