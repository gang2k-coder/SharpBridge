using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Services;

namespace SharpBridge.Tools;

[McpServerToolType]
public class ExecutionTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool, Description("Continue program execution. The program will run until " +
        "a breakpoint is hit, an exception occurs, the program exits, or the timeout elapses. " +
        "On timeout, the program is automatically paused so state can be inspected.")]
    public async Task<string> DebugContinue(
        [Description("Maximum seconds to wait before auto-pausing (default: 30). " +
            "Set to 0 for no timeout (NOT recommended if no breakpoints are set!)")] int timeout = 30,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);
        var stop = await session.ContinueAndWaitAsync(timeout);

        return FormatStopEvent(stop);
    }

    [McpServerTool, Description("Step through code: 'over' (next line, don't enter calls), " +
        "'in' (step into method calls), or 'out' (run until current method returns).")]
    public async Task<string> DebugStep(
        [Description("Step type: 'over', 'in', or 'out'")] string type = "over",
        [Description("Thread ID (uses current thread if omitted)")] int? threadId = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        if (type is not "in" and not "out" and not "over")
            throw new ArgumentException("type must be 'in', 'out', or 'over'");

        var session = _manager.Resolve(sessionId);
        var stop = await session.StepAsync(type, threadId);

        return FormatStopEvent(stop);
    }

    [McpServerTool, Description("Pause a running program immediately. " +
        "Useful when the program is running for too long or to interrupt execution.")]
    public async Task<string> DebugPause(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);
        var stop = await session.PauseAsync();

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
}
