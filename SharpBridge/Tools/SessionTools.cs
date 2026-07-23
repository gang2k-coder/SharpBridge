using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Services;

namespace SharpBridge.Tools;

[McpServerToolType]
public class SessionTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool, Description("Launch a .NET program for debugging. " +
        "The debugger will attempt to stop at the program entry point. " +
        "After launching, always call debug_state to verify the debugger state before using other tools.")]
    public async Task<string> DebugLaunch(
        [Description("Path to the .NET DLL to debug (e.g. bin/Debug/net10.0/MyApp.dll)")] string program,
        [Description("Command-line arguments for the program")] string[]? args = null,
        [Description("Working directory for the launched process")] string? cwd = null,
        [Description("Whether to stop at the program entry point (default: true)")] bool stopAtEntry = true,
        [Description("Environment variables for the launched process")] Dictionary<string, string>? env = null)
    {
        var result = await _manager.CreateAndLaunchAsync(program, args, cwd, stopAtEntry, env);

        if (result.Error is not null)
            throw new InvalidOperationException(result.Error);

        return JsonSerializer.Serialize(new
        {
            status = "launched",
            processId = result.ProcessId,
            processName = result.ProcessName,
            state = result.State,
            note = result.State switch
            {
                "Stopped" => "Program is stopped. Set breakpoints and use debug_continue.",
                "Running" => "Program is running. Use debug_pause to interrupt, or set breakpoints and use debug_continue.",
                "Exited" => "Program has already exited. Check debug output for errors.",
                _ => "Unknown state. Use debug_state to check."
            }
        });
    }

    [McpServerTool, Description("Attach the debugger to a running .NET process by PID or name. " +
        "Provide either processId or processName. If providing a name and multiple processes match, " +
        "you'll get a list of matching PIDs to choose from.")]
    public async Task<string> DebugAttach(
        [Description("Process ID of the running .NET process")] int? processId = null,
        [Description("Process name (e.g. 'TestDebuggee'). Only used if processId is not provided.")] string? processName = null)
    {
        if (processId is null && processName is null)
            throw new ArgumentException("Must provide either processId or processName.");

        SessionAttachResult result;
        if (processId.HasValue)
            result = await _manager.CreateAndAttachByPidAsync(processId.Value);
        else
            result = await _manager.CreateAndAttachByNameAsync(processName!);

        if (result.Error is not null)
            throw new InvalidOperationException(result.Error);

        return JsonSerializer.Serialize(new
        {
            status = result.AlreadyAttached ? "already_attached" : "attached",
            processId = result.ProcessId,
            processName = result.ProcessName,
            state = result.State
        });
    }

    [McpServerTool, Description("Disconnect the debugger from a session and optionally terminate the debuggee.")]
    public string DebugDisconnect(
        [Description("Whether to terminate the debugged process (default: true)")] bool terminateDebuggee = true,
        [Description("Process ID to disconnect. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        _manager.DisconnectSession(sessionId, terminateDebuggee);

        return JsonSerializer.Serialize(new
        {
            status = "disconnected",
            terminated = terminateDebuggee
        });
    }

    [McpServerTool, Description("Get the current debugger state for a session.")]
    public string DebugState(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);

        return JsonSerializer.Serialize(new
        {
            processId = session.ProcessId,
            processName = session.ProcessName,
            state = session.CurrentState.ToString(),
            breakpointCount = session.BreakpointCount,
            info = session.CurrentState switch
            {
                DebugSession.State.NotStarted => "No debug session. Use debug_launch or debug_attach to start.",
                DebugSession.State.Running => "Program is running. Use debug_pause to interrupt or wait for a breakpoint.",
                DebugSession.State.Stopped => "Program is stopped. Use inspection tools (stacktrace_get, variables_get, etc.) or debug_continue/step.",
                DebugSession.State.Exited => "Program has exited. Use debug_disconnect to clean up.",
                _ => ""
            }
        });
    }

    [McpServerTool, Description("Select a debug session as the default for subsequent operations.")]
    public string DebugSelect(
        [Description("Process ID of the session to select")] int processId)
    {
        var info = _manager.SelectSession(processId);

        return JsonSerializer.Serialize(new
        {
            status = "selected",
            processId = info.ProcessId,
            processName = info.ProcessName,
            state = info.State,
            hint = "This session is now the default. All subsequent tools will use it unless you specify a different sessionId."
        });
    }

    [McpServerTool, Description("List all active debug sessions.")]
    public string DebugList()
    {
        var sessions = _manager.ListSessions();

        if (sessions.Count == 0)
            return JsonSerializer.Serialize(new
            {
                count = 0,
                message = "No active debug sessions. Use debug_launch or debug_attach to start one."
            });

        return JsonSerializer.Serialize(new
        {
            count = sessions.Count,
            currentSessionId = _manager.CurrentSessionId,
            sessions = sessions.Select(s => new
            {
                pid = s.ProcessId,
                name = s.ProcessName,
                state = s.State
            })
        });
    }
}
