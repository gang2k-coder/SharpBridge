using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SharpDbg.InMemory;

namespace SharpBridge.Services;

/// <summary>
/// Wraps the DAP debug adapter (SharpDbg) and exposes a request-response API
/// with async event delivery via Channel for MCP tool consumption.
/// </summary>
public class DebugSession : IDisposable
{
    // ===================================================================
    // Pipe handles (from SharpDbgInMemory)
    // ===================================================================
    private Stream? _stdinWriter;   // SharpBridge → writes DAP requests
    private Stream? _stdoutReader;  // SharpBridge → reads DAP responses/events
    private IDisposable? _adapter;  // cleanup for SharpDbg

    // ===================================================================
    // Thread ②: DAP Reader (background task reading stdout pipe)
    // ===================================================================
    private Task? _readerTask;
    private readonly CancellationTokenSource _readerCts = new();
    private int _seqCounter;

    // TCS: seq → response. Thread ② completes, Thread ③ awaits.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pendingRequests = new();

    // Channel: Thread ② writes async DAP events, Thread ③ reads them at need.
    private readonly Channel<DebugEvent> _events =
        Channel.CreateUnbounded<DebugEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    // ===================================================================
    // Thread ③: Session state (owned exclusively by MCP thread — no lock)
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

    // ===================================================================
    // Initialization
    // ===================================================================

    public void Initialize(Action<string>? logAction = null)
    {
        if (CurrentState != State.NotStarted)
            throw new InvalidOperationException("Session already initialized.");

        var (input, output, disposable) = SharpDbgInMemory.NewDebugAdapterStreams(logAction);
        _stdinWriter = input;
        _stdoutReader = output;
        _adapter = disposable;

        // Start Thread ②: continuously read DAP messages from stdout pipe
        _readerTask = Task.Run(() => ReaderLoop(_readerCts.Token));

        // DAP handshake
        var caps = SendRequestSync("initialize", new Dictionary<string, object>
        {
            ["adapterID"] = "sharpbridge",
            ["clientID"] = "sharpbridge-mcp",
            ["clientName"] = "SharpBridge",
            ["locale"] = "en",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["pathFormat"] = "path",
            ["supportsVariableType"] = true,
            ["supportsVariablePaging"] = false,
            ["supportsRunInTerminalRequest"] = false,
            ["supportsMemoryReferences"] = false,
            ["supportsProgressReporting"] = false,
            ["supportsInvalidatedEvent"] = false,
            ["supportsMemoryEvent"] = false,
        });
        _adapterId = caps?["body"]?["name"]?.GetValue<string>() ?? "sharpdbg";

        LogInfo($"DAP initialized. Adapter: {_adapterId}");
    }

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

        // Send launch request
        var launchArgs = new Dictionary<string, object>
        {
            ["program"] = program,
            ["stopAtEntry"] = stopAtEntry,
            ["console"] = "internalConsole",
        };
        if (args is { Length: > 0 }) launchArgs["args"] = args;
        if (cwd is not null) launchArgs["cwd"] = cwd;
        if (env is { Count: > 0 }) launchArgs["env"] = env;

        SendRequestSync("launch", launchArgs);

        // ConfigurationDone triggers the actual process start
        SendRequestSync("configurationDone");

        CurrentState = State.Running;

        if (stopAtEntry)
        {
            await WaitForStopAsync(ct);
        }
    }

    public async Task AttachAsync(int processId, CancellationToken ct = default)
    {
        if (CurrentState != State.NotStarted)
            throw new InvalidOperationException("Session not in correct state for attach.");

        SendRequestSync("attach", new Dictionary<string, object>
        {
            ["processId"] = processId
        });

        SendRequestSync("configurationDone");

        // After attach, DebugActiveProcess(pid, false) suspends all threads.
        // The process is stopped immediately — no StoppedEvent is sent for attach.
        CurrentState = State.Stopped;
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
    }

    public IReadOnlyList<BreakpointEntry> SetBreakpoints(
        string filePath, params (int Line, int? Column, string? Condition, string? HitCondition)[] breakpoints)
    {
        // Clear existing BPs for this file
        _breakpointsByFile.Remove(filePath);

        var entries = new List<BreakpointEntry>();
        var dapBreakpoints = new List<Dictionary<string, object>>();

        foreach (var (line, col, cond, hitCond) in breakpoints)
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
                EndColumn: null);

            entries.Add(entry);

            var sourceBp = new Dictionary<string, object> { ["line"] = line };
            if (col.HasValue) sourceBp["column"] = col.Value;
            if (cond is not null) sourceBp["condition"] = cond;
            if (hitCond is not null) sourceBp["hitCondition"] = hitCond;
            dapBreakpoints.Add(sourceBp);
        }

        _breakpointsByFile[filePath] = entries;

        // Send to DAP
        var args = new Dictionary<string, object>
        {
            ["source"] = new Dictionary<string, object> { ["path"] = filePath },
            ["breakpoints"] = dapBreakpoints
        };

        var response = SendRequestSync("setBreakpoints", args);
        var bpResults = response?["body"]?["breakpoints"]?.AsArray();

        // Update verification status from response
        if (bpResults is not null)
        {
            for (int i = 0; i < Math.Min(entries.Count, bpResults.Count); i++)
            {
                var result = bpResults[i];
                if (result is JsonObject obj)
                {
                    entries[i].Verified = obj["verified"]?.GetValue<bool>() ?? false;
                    entries[i].Message = obj["message"]?.GetValue<string>();
                    if (obj["line"] is not null) entries[i] = entries[i] with { Line = obj["line"]!.GetValue<int>() };
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
                // Re-send remaining BPs for this file
                if (entries.Count == 0)
                {
                    _breakpointsByFile.Remove(file);
                    // Send empty set to clear file
                    SendRequestSync("setBreakpoints", new Dictionary<string, object>
                    {
                        ["source"] = new Dictionary<string, object> { ["path"] = file },
                        ["breakpoints"] = Array.Empty<object>()
                    });
                }
                else
                {
                    SetBreakpoints(file, entries.Select(e =>
                        (e.Line, e.Column, e.Condition, e.HitCondition)).ToArray());
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
    // Execution Control
    // ===================================================================

    public async Task<StopEvent> ContinueAndWaitAsync(
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        // Pre-check
        if (BreakpointCount == 0 && timeoutSeconds == 0)
        {
            throw new InvalidOperationException(
                "No breakpoints set and timeout is disabled (0 = infinite). " +
                "The program may run indefinitely. " +
                "Set a breakpoint with breakpoint_set first, " +
                "or specify a timeout value (e.g. timeout=30).");
        }

        if (CurrentState != State.Stopped)
            throw new InvalidOperationException($"Cannot continue: debugger state is {CurrentState}.");

        CurrentState = State.Running;
        SendRequest("continue", new Dictionary<string, object> { ["threadId"] = LastStop.ThreadId });

        try
        {
            return await WaitForStopWithTimeoutAsync(timeoutSeconds, ct);
        }
        catch (OperationCanceledException)
        {
            // Force-pause the debuggee
            SendRequest("pause", new Dictionary<string, object> { ["threadId"] = LastStop.ThreadId });
            var stop = await WaitForStopAsync(CancellationToken.None);
            return stop with
            {
                Note = ct.IsCancellationRequested
                    ? "Cancelled by user. Program was paused."
                    : $"Timed out after {timeoutSeconds}s. Program was paused."
            };
        }
    }

    public async Task<StopEvent> StepAsync(
        string type, int? threadId = null, CancellationToken ct = default)
    {
        if (CurrentState != State.Stopped)
            throw new InvalidOperationException($"Cannot step: debugger state is {CurrentState}.");

        CurrentState = State.Running;
        var command = type switch
        {
            "in" => "stepIn",
            "out" => "stepOut",
            _ => "next" // "over"
        };

        var args = new Dictionary<string, object>();
        if (threadId.HasValue) args["threadId"] = threadId.Value;
        SendRequest(command, args);

        return await WaitForStopAsync(ct);
    }

    public async Task<StopEvent> PauseAsync(CancellationToken ct = default)
    {
        if (CurrentState != State.Running)
            throw new InvalidOperationException($"Cannot pause: debugger state is {CurrentState}.");

        SendRequest("pause", new Dictionary<string, object>());
        return await WaitForStopAsync(ct);
    }

    // ===================================================================
    // Inspection (only valid when STOPPED)
    // ===================================================================

    public List<ThreadInfo> GetThreads()
    {
        EnsureStopped();
        var response = SendRequestSync("threads");
        var threads = response?["body"]?["threads"]?.AsArray() ?? [];

        return threads.Select(t =>
        {
            var obj = (JsonObject)t;
            return new ThreadInfo(
                obj["id"]!.GetValue<int>(),
                obj["name"]?.GetValue<string>() ?? $"Thread {obj["id"]!.GetValue<int>()}");
        }).ToList();
    }

    public List<StackFrameInfo> GetStackTrace(int threadId, int startFrame = 0, int? levels = null)
    {
        EnsureStopped();
        var args = new Dictionary<string, object>
        {
            ["threadId"] = threadId,
            ["startFrame"] = startFrame
        };
        if (levels.HasValue) args["levels"] = levels.Value;

        var response = SendRequestSync("stackTrace", args);
        var frames = response?["body"]?["stackFrames"]?.AsArray() ?? [];

        return frames.Select(f =>
        {
            var obj = (JsonObject)f;
            var src = obj["source"] as JsonObject;
            return new StackFrameInfo(
                obj["id"]!.GetValue<int>(),
                obj["name"]!.GetValue<string>(),
                src?["path"]?.GetValue<string>(),
                obj["line"]!.GetValue<int>(),
                obj["column"]?.GetValue<int>() ?? 0,
                obj["endLine"]?.GetValue<int>() ?? 0,
                obj["endColumn"]?.GetValue<int>() ?? 0);
        }).ToList();
    }

    public List<VariableInfo> GetVariablesForFrame(int frameId)
    {
        EnsureStopped();
        // First get scopes for the frame
        var scopes = GetScopes(frameId);
        if (scopes.Count == 0) return [];

        // Get variables from the "Locals" scope
        var localsScope = scopes.FirstOrDefault(s => s.Name == "Locals") ?? scopes[0];
        return ExpandVariables(localsScope.VariablesReference);
    }

    public List<VariableInfo> ExpandVariables(int variablesReference)
    {
        EnsureStopped();
        var response = SendRequestSync("variables", new Dictionary<string, object>
        {
            ["variablesReference"] = variablesReference
        });
        var vars = response?["body"]?["variables"]?.AsArray() ?? [];

        return vars.Select(v =>
        {
            var obj = (JsonObject)v;
            return new VariableInfo(
                obj["name"]!.GetValue<string>(),
                obj["value"]!.GetValue<string>(),
                obj["type"]?.GetValue<string>(),
                obj["variablesReference"]?.GetValue<int>() ?? 0,
                obj["evaluateName"]?.GetValue<string>(),
                obj["indexedVariables"]?.GetValue<int>(),
                obj["namedVariables"]?.GetValue<int>());
        }).ToList();
    }

    private List<ScopeInfo> GetScopes(int frameId)
    {
        var response = SendRequestSync("scopes", new Dictionary<string, object>
        {
            ["frameId"] = frameId
        });
        var scopes = response?["body"]?["scopes"]?.AsArray() ?? [];

        return scopes.Select(s =>
        {
            var obj = (JsonObject)s;
            return new ScopeInfo(
                obj["name"]!.GetValue<string>(),
                obj["variablesReference"]!.GetValue<int>(),
                obj["expensive"]?.GetValue<bool>() ?? false);
        }).ToList();
    }

    public async Task<EvalResult> EvaluateAsync(string expression, int? frameId = null)
    {
        EnsureStopped();
        var args = new Dictionary<string, object>
        {
            ["expression"] = expression,
            ["context"] = "repl"
        };
        if (frameId.HasValue) args["frameId"] = frameId.Value;

        var response = SendRequestSync("evaluate", args);
        var body = response?["body"] as JsonObject;

        return new EvalResult(
            body?["result"]?.GetValue<string>() ?? "",
            body?["type"]?.GetValue<string>(),
            body?["variablesReference"]?.GetValue<int>() ?? 0);
    }

    public ExceptionDetail? GetExceptionInfo(int? threadId = null)
    {
        EnsureStopped();
        var args = new Dictionary<string, object>();
        if (threadId.HasValue) args["threadId"] = threadId.Value;

        try
        {
            var response = SendRequestSync("exceptionInfo", args);
            var body = response?["body"] as JsonObject;
            if (body is null) return null;

            return new ExceptionDetail(
                body["exceptionId"]?.GetValue<string>() ?? "",
                body["description"]?.GetValue<string>() ?? "",
                body["breakMode"]?.GetValue<string>() ?? "",
                body["details"]?["message"]?.GetValue<string>(),
                body["details"]?["typeName"]?.GetValue<string>(),
                body["details"]?["fullTypeName"]?.GetValue<string>(),
                body["details"]?["stackTrace"]?.GetValue<string>(),
                body["details"]?["formattedDescription"]?.GetValue<string>());
        }
        catch
        {
            // No exception on current thread
            return null;
        }
    }

    // ===================================================================
    // Disconnect
    // ===================================================================

    public void Disconnect(bool terminateDebuggee = true)
    {
        if (CurrentState == State.NotStarted) return;

        try
        {
            SendRequestSync("disconnect", new Dictionary<string, object>
            {
                ["terminateDebuggee"] = terminateDebuggee
            });
        }
        catch { /* ignore protocol errors during shutdown */ }

        Cleanup();
    }

    public void Dispose()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        CurrentState = State.NotStarted;
        _readerCts.Cancel();
        _events.Writer.TryComplete();
        _adapter?.Dispose();
        _stdinWriter = null;
        _stdoutReader = null;
    }

    // ===================================================================
    // DAP Protocol Client (Thread ② + Thread ③ shared infrastructure)
    // ===================================================================

    /// <summary>
    /// Thread ③: Send a DAP request and return the response synchronously.
    /// Blocks the MCP thread until Thread ② reads and matches the response.
    /// </summary>
    private JsonObject? SendRequestSync(string command, object? args = null)
        => SendRequestSync(command, args, CancellationToken.None);

    private JsonObject? SendRequestSync(string command, object? args, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seq = Interlocked.Increment(ref _seqCounter);
        _pendingRequests[seq] = tcs;

        WriteDapMessage(new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = args is not null
                ? JsonSerializer.SerializeToNode(args)
                : null
        });

        LogInfo($"DAP ← [{seq}] {command}");

        try
        {
            if (ct.CanBeCanceled)
                tcs.Task.Wait(ct);
            else
            {
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException($"DAP request '{command}' timed out after 60s");
            }

            LogInfo($"DAP → [{seq}] {command} OK");
            return tcs.Task.Result;
        }
        catch (AggregateException) when (tcs.Task.IsFaulted)
        {
            throw tcs.Task.Exception!.InnerException!;
        }
        finally
        {
            _pendingRequests.TryRemove(seq, out _);
        }
    }

    /// <summary>
    /// Thread ③: Fire-and-forget DAP request (for continue, step, pause).
    /// </summary>
    private void SendRequest(string command, object? args = null)
    {
        var seq = Interlocked.Increment(ref _seqCounter);
        WriteDapMessage(new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = args is not null
                ? JsonSerializer.SerializeToNode(args)
                : null
        });
    }

    private void WriteDapMessage(JsonObject message)
    {
        var json = JsonSerializer.Serialize(message);
        var header = $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
        _stdinWriter!.Write(System.Text.Encoding.UTF8.GetBytes(header + json));
        _stdinWriter.Flush();
    }

    // ===================================================================
    // Thread ②: Background reader loop
    // ===================================================================

    private async Task ReaderLoop(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_stdoutReader!, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            while (!ct.IsCancellationRequested)
            {
                var contentTypeLine = await reader.ReadLineAsync(ct);
                if (contentTypeLine == null) break; // stream closed

                // Parse Content-Length header
                if (!contentTypeLine.StartsWith("Content-Length:"))
                {
                    LogInfo($"Unexpected header: {contentTypeLine}");
                    continue;
                }

                var lengthStr = contentTypeLine["Content-Length:".Length..].Trim();
                if (!int.TryParse(lengthStr, out var contentLength))
                {
                    LogInfo($"Bad Content-Length: {lengthStr}");
                    continue;
                }

                // Skip the blank line separator
                await reader.ReadLineAsync(ct);

                // Read the JSON body
                var buffer = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var count = await reader.ReadAsync(buffer, read, contentLength - read);
                    read += count;
                }
                var json = new string(buffer, 0, contentLength);

                var message = JsonNode.Parse(json) as JsonObject;
                if (message is null) continue;

                var type = message["type"]?.GetValue<string>();
                var cmd = message["command"]?.GetValue<string>() ?? "";
                var rSeq = message["request_seq"]?.GetValue<int>() ?? 0;
                var evtName = message["event"]?.GetValue<string>() ?? "";
                LogInfo($"DAP pipe ← type={type}, command={cmd}, req_seq={rSeq}, event={evtName}");

                if (type == "response")
                {
                    var seq = message["request_seq"]?.GetValue<int>() ?? 0;
                    if (_pendingRequests.TryRemove(seq, out var tcs))
                    {
                        if (message["success"]?.GetValue<bool>() == false)
                        {
                            var errorMsg = message["message"]?.GetValue<string>() ?? "DAP request failed";
                            tcs.TrySetException(new InvalidOperationException(
                                $"{message["command"]?.GetValue<string>()}: {errorMsg}"));
                        }
                        else
                        {
                            tcs.TrySetResult(message);
                        }
                    }
                }
                else if (type == "event")
                {
                    var evt = DebugEvent.FromDap(message);
                    _events.Writer.TryWrite(evt);
                }
                // else: ignore other types
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            LogInfo($"ReaderLoop error: {ex}");
        }
        finally
        {
            // Signal any waiting requests that the reader is dead
            foreach (var (_, tcs) in _pendingRequests)
                tcs.TrySetException(new InvalidOperationException("DAP reader terminated unexpectedly."));
            _pendingRequests.Clear();
        }
    }

    // ===================================================================
    // Thread ③: Event consumption helpers
    // ===================================================================

    private async Task<StopEvent> WaitForStopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _events.Reader.WaitToReadAsync(ct);
            if (!_events.Reader.TryRead(out var evt)) continue;

            switch (evt.Kind)
            {
                case DebugEventKind.Stopped:
                    CurrentState = State.Stopped;
                    _lastStop = new StopEvent(
                        "stopped", evt.ThreadId, evt.AllThreadsStopped,
                        evt.Reason, evt.FilePath, evt.Line, evt.Column);
                    return _lastStop;

                case DebugEventKind.Exited:
                    CurrentState = State.Exited;
                    _lastStop = new StopEvent("exited", null, null, null, null, 0, 0)
                    { ExitCode = evt.ExitCode };
                    return _lastStop;

                case DebugEventKind.Terminated:
                    CurrentState = State.Exited;
                    _lastStop = new StopEvent("terminated", null, null, null, null, 0, 0);
                    return _lastStop;

                case DebugEventKind.Output:
                    _outputLog.Add(evt.Text ?? "");
                    break; // keep waiting
            }
        }
    }

    private async Task<StopEvent> WaitForStopWithTimeoutAsync(int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await WaitForStopAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw; // re-throw, caller handles timeout
        }
    }

    public void DrainPendingEvents()
    {
        while (_events.Reader.TryRead(out var evt))
        {
            switch (evt.Kind)
            {
                case DebugEventKind.Exited:
                case DebugEventKind.Terminated:
                    CurrentState = State.Exited;
                    _lastStop = new StopEvent(
                        evt.Kind == DebugEventKind.Exited ? "exited" : "terminated",
                        null, null, null, null, 0, 0);
                    break;
                case DebugEventKind.Output:
                    _outputLog.Add(evt.Text ?? "");
                    break;
                case DebugEventKind.Stopped:
                    CurrentState = State.Stopped;
                    _lastStop = new StopEvent("stopped", evt.ThreadId, evt.AllThreadsStopped,
                        evt.Reason, evt.FilePath, evt.Line, evt.Column);
                    break;
            }
        }
    }

    private void EnsureStopped()
    {
        DrainPendingEvents();
        if (CurrentState != State.Stopped)
            throw new InvalidOperationException(
                $"Debugger is not stopped (state: {CurrentState}). Use debug_state first.");
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

public record ThreadInfo(int Id, string Name);
public record StackFrameInfo(
    int Id, string Name, string? Source,
    int Line, int Column, int EndLine, int EndColumn);
public record ScopeInfo(string Name, int VariablesReference, bool Expensive);
public record VariableInfo(
    string Name, string Value, string? Type, int VariablesReference,
    string? EvaluateName, int? IndexedVariables, int? NamedVariables);
public record EvalResult(string Result, string? Type, int VariablesReference);
public record ExceptionDetail(
    string ExceptionId, string Description, string BreakMode,
    string? Message, string? TypeName, string? FullTypeName,
    string? StackTrace, string? FormattedDescription);

// ===================================================================
// Internal: DAP event wrapper for Channel
// ===================================================================

public enum DebugEventKind { Stopped, Exited, Terminated, Output }

public record DebugEvent
{
    public DebugEventKind Kind { get; init; }
    public int? ThreadId { get; init; }
    public bool? AllThreadsStopped { get; init; }
    public string? Reason { get; init; }
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? ExitCode { get; init; }
    public string? Text { get; init; }

    public static DebugEvent FromDap(JsonObject message)
    {
        var eventType = message["event"]?.GetValue<string>() ?? "";
        var body = message["body"] as JsonObject;

        return eventType switch
        {
            "stopped" => new DebugEvent
            {
                Kind = DebugEventKind.Stopped,
                ThreadId = body?["threadId"]?.GetValue<int>(),
                AllThreadsStopped = body?["allThreadsStopped"]?.GetValue<bool>(),
                Reason = body?["reason"]?.GetValue<string>(),
                FilePath = body?["source"]?["path"]?.GetValue<string>(),
                Line = body?["line"]?.GetValue<int>() ?? 0,
                Column = body?["column"]?.GetValue<int>() ?? 0
            },
            "exited" => new DebugEvent
            {
                Kind = DebugEventKind.Exited,
                ExitCode = body?["exitCode"]?.GetValue<int>()
            },
            "terminated" => new DebugEvent { Kind = DebugEventKind.Terminated },
            "output" => new DebugEvent
            {
                Kind = DebugEventKind.Output,
                Text = body?["output"]?.GetValue<string>()
            },
            "thread" => new DebugEvent { Kind = DebugEventKind.Output }, // treat as info
            "module" => new DebugEvent { Kind = DebugEventKind.Output },
            "breakpoint" => new DebugEvent { Kind = DebugEventKind.Output },
            "continued" => new DebugEvent { Kind = DebugEventKind.Output },
            _ => new DebugEvent { Kind = DebugEventKind.Output, Text = $"[{eventType}]" }
        };
    }
}
