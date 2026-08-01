using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Infrastructure.Attributes;
using SharpBridge.Services;
using SharpBridge.State;

namespace SharpBridge.Tools;

[McpServerToolType]
public class ExecutionTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped)]
    [Description("Continue program execution. Runs until a breakpoint, " +
        "exception, or exit. If capture-action breakpoints are set, they will " +
        "auto-capture variables and continue silently — use get_captures afterwards. " +
        "When a break-action breakpoint or non-breakpoint stop occurs, returns the stop event. " +
        "On timeout, returns status 'running' without pausing — use debug_wait to keep " +
        "waiting for a breakpoint, or debug_pause to interrupt and inspect state.")]
    public async Task<string> DebugContinue(
        [Description("Maximum seconds to wait before auto-pausing (default: 30). " +
            "Set to 0 for no timeout (NOT recommended if no breakpoints are set!)")] int timeout = 30,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var stop = await session.ContinueAndWaitAsync(timeout);

        return FormatStopEvent(stop);
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Step through code: 'over' (next line, don't enter calls), " +
        "'in' (step into method calls), or 'out' (run until current method returns).")]
    public async Task<string> DebugStep(
        [Description("Step type: 'over', 'in', or 'out'")] string type = "over",
        [Description("Thread ID from threads_list. Defaults to the thread that triggered the current stop.")] int? threadId = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        if (type is not "in" and not "out" and not "over")
            throw new ArgumentException("type must be 'in', 'out', or 'over'");

        var session = ResolveSession(processId, processName);
        var stop = await session.StepAsync(type, threadId);

        return FormatStopEvent(stop);
    }

    [McpServerTool]
    [AllowedState(SessionState.Running)]
    [Description("Pause a running program immediately. " +
        "Requires the program to be in Running state (check with debug_state first). " +
        "Useful when execution runs too long or to interrupt.")]
    public async Task<string> DebugPause(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var stop = await session.PauseAsync();

        return FormatStopEvent(stop);
    }

    [McpServerTool]
    [AllowedState(SessionState.Running)]
    [Description("Wait for the program to stop (breakpoint hit, exception, exit) " +
        "without sending any execution command. Use when the process is already running " +
        "(e.g. after debug_continue timed out). Returns stop event on break/exit, " +
        "or running status on timeout.")]
    public async Task<string> DebugWait(
        [Description("Maximum seconds to wait (default: 30)")] int timeout = 30,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var stop = await session.WaitAndWaitAsync(timeout);

        return FormatStopEvent(stop);
    }

    private static string FormatStopEvent(StopEvent stop)
    {
        return JsonSerializer.Serialize(new
        {
            status = stop.Status,
            reason = stop.Reason,
            threadId = stop.ThreadId,
            allThreadsStopped = stop.AllThreadsStopped,
            source = stop.FilePath is not null ? new
            {
                path = stop.FilePath,
                line = stop.Line,
                column = stop.Column
            } : null,
            exitCode = stop.ExitCode,
            note = stop.Note,
            state = stop.Status switch
            {
                "stopped" => "Program stopped. Use inspection tools to examine state.",
                "exited" => "Program exited. Use debug_disconnect to clean up.",
                _ => ""
            }
        });
    }

    private DebugSession ResolveSession(int? processId, string? processName)
    {
        if (processId.HasValue)
            return _manager.Resolve(processId.Value);
        if (!string.IsNullOrWhiteSpace(processName))
            return _manager.Resolve(processName);
        return _manager.Resolve(processId: null);
    }
}
