using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Services;

namespace SharpBridge.Tools;

[McpServerToolType]
public class SessionTools(DebugSession session)
{
    private readonly DebugSession _session = session;

    [McpServerTool, Description("Launch a .NET program for debugging. " +
        "The program will start suspended, allowing breakpoints to be set before execution begins.")]
    public async Task<string> DebugLaunch(
        [Description("Path to the .NET DLL to debug (e.g. bin/Debug/net10.0/MyApp.dll)")] string program,
        [Description("Command-line arguments for the program")] string[]? args = null,
        [Description("Working directory for the launched process")] string? cwd = null,
        [Description("Whether to stop at the program entry point (default: true)")] bool stopAtEntry = true,
        [Description("Environment variables for the launched process")] Dictionary<string, string>? env = null)
    {
        if (_session.CurrentState != DebugSession.State.NotStarted)
            throw new InvalidOperationException(
                $"Cannot launch: session is already {_session.CurrentState}. Use debug_disconnect first.");

        _session.Initialize();
        await _session.LaunchAsync(program, args, cwd, stopAtEntry, env);

        return JsonSerializer.Serialize(new
        {
            status = "launched",
            program,
            state = _session.CurrentState.ToString(),
            note = stopAtEntry
                ? "Program started and stopped at entry. Set breakpoints, then use debug_continue."
                : "Program is running. Use debug_pause to interrupt."
        });
    }

    [McpServerTool, Description("Attach the debugger to a running .NET process by PID.")]
    public async Task<string> DebugAttach(
        [Description("Process ID of the running .NET process")] int processId)
    {
        if (_session.CurrentState != DebugSession.State.NotStarted)
            throw new InvalidOperationException(
                $"Cannot attach: session is already {_session.CurrentState}. Use debug_disconnect first.");

        _session.Initialize();
        await _session.AttachAsync(processId);

        return JsonSerializer.Serialize(new
        {
            status = "attached",
            processId,
            state = _session.CurrentState.ToString()
        });
    }

    [McpServerTool, Description("Disconnect the debugger and optionally terminate the debuggee.")]
    public string DebugDisconnect(
        [Description("Whether to terminate the debugged process (default: true)")] bool terminateDebuggee = true)
    {
        if (_session.CurrentState == DebugSession.State.NotStarted)
            return JsonSerializer.Serialize(new { status = "not_connected" });

        _session.Disconnect(terminateDebuggee);

        return JsonSerializer.Serialize(new
        {
            status = "disconnected",
            terminated = terminateDebuggee
        });
    }

    [McpServerTool, Description("Get the current debugger state: running, stopped, exited, or disconnected. " +
        "Always call this first if a previous operation returned an error.")]
    public string DebugState()
    {
        _session.DrainPendingEvents();

        return JsonSerializer.Serialize(new
        {
            state = _session.CurrentState.ToString(),
            breakpointCount = _session.BreakpointCount,
            info = _session.CurrentState switch
            {
                DebugSession.State.NotStarted => "No debug session. Use debug_launch or debug_attach to start.",
                DebugSession.State.Running => "Program is running. Use debug_pause to interrupt or wait for a breakpoint.",
                DebugSession.State.Stopped => "Program is stopped. Use inspection tools (stacktrace_get, variables_get, etc.) or debug_continue/step.",
                DebugSession.State.Exited => "Program has exited. Use debug_disconnect to clean up.",
                _ => ""
            }
        });
    }
}
