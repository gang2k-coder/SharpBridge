using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SharpBridge.Services;

/// <summary>
/// Manages multiple DebugSession instances, keyed by process ID.
/// Routes MCP tool requests to the correct session.
/// </summary>
public class DebugSessionManager : IDisposable
{
    private readonly ILogger<DebugSession> _logger;
    private readonly ConcurrentDictionary<int, DebugSession> _sessions = new();
    private int? _currentSessionId;

    public DebugSessionManager(ILogger<DebugSession> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The currently selected default session ID (set via debug_select).
    /// </summary>
    public int? CurrentSessionId
    {
        get => _currentSessionId;
        private set => _currentSessionId = value;
    }

    // ===================================================================
    // Session routing
    // ===================================================================

    /// <summary>
    /// Resolve the target session by process ID.
    /// Falls back to CurrentSessionId if processId is null.
    /// </summary>
    public DebugSession Resolve(int? processId)
    {
        var targetId = processId ?? CurrentSessionId;
        if (targetId is null)
            throw new InvalidOperationException(
                "No debug session. Use debug_launch, debug_attach, or debug_select first.");

        if (_sessions.TryGetValue(targetId.Value, out var session))
            return session;

        throw new InvalidOperationException(
            $"Session for PID {targetId.Value} not found. " +
            "It may have exited. Use debug_list to see active sessions.");
    }

    /// <summary>
    /// Resolve the target session by process name.
    /// Searches existing sessions first, then queries the OS.
    /// Requires exactly one matching process.
    /// </summary>
    public DebugSession Resolve(string processName)
    {
        // 1. Search existing sessions by ProcessName
        var existing = _sessions.Values
            .FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        // 2. Query OS for running processes by name
        System.Diagnostics.Process[] procs;
        try
        {
            procs = System.Diagnostics.Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot query processes by name '{processName}': {ex.Message}");
        }

        if (procs.Length == 0)
            throw new InvalidOperationException(
                $"No process named '{processName}' is running. Start the program first, then use debug_attach.");

        if (procs.Length > 1)
            throw new InvalidOperationException(
                $"Multiple processes named '{processName}' found. Use debug_attach with a specific processId instead.");

        var pid = procs[0].Id;
        try { foreach (var p in procs) p.Dispose(); } catch { }

        throw new InvalidOperationException(
            $"No debug session for PID {pid} ('{processName}'). Use debug_attach processName=\"{processName}\" first.");
    }

    // ===================================================================
    // Launch
    // ===================================================================

    public async Task<SessionLaunchResult> CreateAndLaunchAsync(
        string program,
        string[]? args = null,
        string? cwd = null,
        bool stopAtEntry = true,
        Dictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var session = new DebugSession(_logger,
            onDisposed: OnSessionDisposed,
            onError: OnSessionError);


        try
        {
            await session.LaunchAsync(program, args, cwd, stopAtEntry, env, ct);
        }
        catch (Exception ex)
        {
            session.Dispose();
            return new SessionLaunchResult(null, null, "Failed")
            {
                Error = $"Launch failed: {ex.Message}"
            };
        }

        // PID should have been captured from SharpDbg log output.
        // If still null, the log format may have changed (known issue).
        if (session.ProcessId is null)
        {
            session.Disconnect(terminateDebuggee: true);
            return new SessionLaunchResult(null, null, "Failed")
            {
                Error = "Could not determine PID of launched process. " +
                        "This is a known issue — SharpDbg log format may have changed."
            };
        }

        _sessions[session.ProcessId.Value] = session;
        return new SessionLaunchResult(
            session.ProcessId,
            session.ProcessName,
            session.CurrentState.ToString());
    }

    // ===================================================================
    // Attach by PID
    // ===================================================================

    public async Task<SessionAttachResult> CreateAndAttachByPidAsync(
        int processId, CancellationToken ct = default)
    {
        // Idempotent: if already attached to this PID, return it
        if (_sessions.TryGetValue(processId, out var existing))
            return new SessionAttachResult(
                processId, existing.ProcessName, existing.CurrentState.ToString())
            { AlreadyAttached = true };

        // Verify the process exists in the OS
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new SessionAttachResult(processId, null, "NotFound")
            {
                Error = $"No process with PID {processId} found on this system."
            };
        }
        catch (Exception ex)
        {
            return new SessionAttachResult(processId, null, "Error")
            {
                Error = $"Cannot access process {processId}: {ex.Message}"
            };
        }

        var session = new DebugSession(_logger,
            onDisposed: OnSessionDisposed,
            onError: OnSessionError);


        try
        {
            await session.AttachAsync(processId, ct);
        }
        catch (Exception ex)
        {
            session.Dispose();
            return new SessionAttachResult(processId, null, "Failed")
            {
                Error = $"Attach failed: {ex.Message}"
            };
        }

        _sessions[processId] = session;
        return new SessionAttachResult(
            processId, session.ProcessName, session.CurrentState.ToString());
    }

    // ===================================================================
    // Attach by process name
    // ===================================================================

    public async Task<SessionAttachResult> CreateAndAttachByNameAsync(
        string processName, CancellationToken ct = default)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(processName);

        if (procs.Length == 0)
        {
            return new SessionAttachResult(null, processName, "NotFound")
            {
                Error = $"No process named '{processName}' found on this system."
            };
        }

        if (procs.Length > 1)
        {
            var pidList = string.Join("\n", procs.Select(p =>
                $"  PID {p.Id} - {p.ProcessName}"));
            return new SessionAttachResult(null, processName, "Ambiguous")
            {
                Error = $"Multiple processes named '{processName}' found. " +
                        $"Use debug_attach with a specific processId:\n{pidList}"
            };
        }

        // Exactly one match — attach to it
        var pid = procs[0].Id;
        try
        {
            foreach (var p in procs) p.Dispose();
        }
        catch { }

        return await CreateAndAttachByPidAsync(pid, ct);
    }

    // ===================================================================
    // Session selection
    // ===================================================================

    public SessionInfo SelectSession(int processId)
    {
        if (!_sessions.TryGetValue(processId, out var session))
        {
            throw new InvalidOperationException(
                $"No session for PID {processId}. Use debug_list to see active sessions.");
        }

        CurrentSessionId = processId;
        return new SessionInfo(
            session.ProcessId!.Value,
            session.ProcessName ?? "unknown",
            session.CurrentState.ToString());
    }

    // ===================================================================
    // List sessions
    // ===================================================================

    public List<SessionInfo> ListSessions()
    {
        return _sessions.Select(kvp =>
            new SessionInfo(
                kvp.Key,
                kvp.Value.ProcessName ?? "unknown",
                kvp.Value.CurrentState.ToString()))
            .OrderBy(s => s.ProcessId)
            .ToList();
    }

    // ===================================================================
    // Disconnect a session
    // ===================================================================

    public void DisconnectSession(int? sessionId, bool terminateDebuggee = true)
    {
        var targetId = sessionId ?? CurrentSessionId;
        if (targetId is null)
            throw new InvalidOperationException(
                "No debug session. Use debug_launch, debug_attach, or debug_select first.");

        if (_sessions.TryRemove(targetId.Value, out var session))
        {
            session.Disconnect(terminateDebuggee);
        }

        if (CurrentSessionId == targetId)
            CurrentSessionId = null;
    }

    // ===================================================================
    // Lifecycle callbacks
    // ===================================================================

    private void OnSessionDisposed(int pid)
    {
        _sessions.TryRemove(pid, out _);
        if (CurrentSessionId == pid)
            CurrentSessionId = null;
    }

    private void OnSessionError(int pid, Exception ex)
    {
        Debug.WriteLine($"[DebugSessionManager] Session {pid} error: {ex.Message}");
        _sessions.TryRemove(pid, out _);
        if (CurrentSessionId == pid)
            CurrentSessionId = null;
    }

    // ===================================================================
    // Dispose all sessions
    // ===================================================================

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _sessions.Clear();
        CurrentSessionId = null;
    }
}

// ===================================================================
// Result types
// ===================================================================

public record SessionInfo(int ProcessId, string ProcessName, string State);

public record SessionLaunchResult(int? ProcessId, string? ProcessName, string State)
{
    public string? Error { get; init; }
}

public record SessionAttachResult(int? ProcessId, string? ProcessName, string State)
{
    public string? Error { get; init; }
    public bool AlreadyAttached { get; init; }
}
