using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Services;

namespace SharpBridge.Tools;

[McpServerToolType]
public class BreakpointTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool, Description("Set a breakpoint at a specific file and line. " +
        "Supports conditional breakpoints, hit count conditions, auto-capture, and go/break action. " +
        "Returns the breakpoint ID for later removal.")]
    public string BreakpointSet(
        [Description("Path to the source file (absolute or relative to the debugged program)")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Optional column number (1-based)")] int? column = null,
        [Description("Optional C# expression that must be true for the breakpoint to trigger")] string? condition = null,
        [Description("Optional hit count condition (e.g. '>=5', '==3', '%2')")] string? hitCondition = null,
        [Description("'break' = stop and wait (default), 'go' = auto-continue after capture")] string action = "break",
        [Description("Enable auto-capture of variables when this breakpoint hits")] bool capture = false,
        [Description("Capture scope (only when capture=true): 'locals', 'arguments', or 'all' (default)")] string captureScope = "all",
        [Description("Capture expansion depth (only when capture=true): 0=summary, 1+=expand children")] int captureDepth = 0,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);

        var entries = session.SetBreakpoints(filePath,
            (line, column, condition, hitCondition, action, capture, captureScope, captureDepth));
        var entry = entries.FirstOrDefault()!;

        return JsonSerializer.Serialize(new
        {
            id = entry.Id,
            filePath = entry.FilePath,
            line = entry.Line,
            column = entry.Column,
            verified = entry.Verified,
            message = entry.Message ?? (entry.Verified ? "Breakpoint is set and will be hit." : "Breakpoint could not be verified. It may not be in executable code."),
            condition = entry.Condition,
            hitCondition = entry.HitCondition,
            action = entry.Action,
            capture = entry.Capture,
            captureScope = entry.CaptureScope,
            captureDepth = entry.CaptureDepth,
            hint = entry.Verified
                ? (entry.Action == "go" ? "Go-action: will auto-continue after hitting." : null)
                : "The breakpoint may be on a line that doesn't map to any IL instruction (e.g. a blank line or comment). Check the source line number."
        });
    }

    [McpServerTool, Description("Remove a breakpoint by its ID (returned from breakpoint_set).")]
    public string BreakpointRemove(
        [Description("The breakpoint ID to remove")] int id,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);
        var removed = session.RemoveBreakpoint(id);

        return JsonSerializer.Serialize(new
        {
            id,
            removed,
            message = removed ? "Breakpoint removed." : $"Breakpoint {id} not found."
        });
    }

    [McpServerTool, Description("List all currently set breakpoints with their status, conditions, and capture configuration.")]
    public string BreakpointList(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);
        var bps = session.GetAllBreakpoints();

        return JsonSerializer.Serialize(new
        {
            count = bps.Count,
            breakpoints = bps.Select(bp => new
            {
                bp.Id,
                bp.FilePath,
                bp.Line,
                bp.Column,
                bp.Verified,
                bp.Message,
                bp.Condition,
                bp.HitCondition,
                bp.Action,
                bp.Capture,
                bp.CaptureScope,
                bp.CaptureDepth,
                hint = bp.Action == "go"
                    ? "Go-action: auto-continues after hitting (with capture if enabled)"
                    : null
            })
        });
    }
}
