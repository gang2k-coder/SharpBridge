using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Services;

namespace SharpBridge.Tools;

[McpServerToolType]
public class BreakpointTools(DebugSession session)
{
    private readonly DebugSession _session = session;

    [McpServerTool, Description("Set a breakpoint at a specific file and line. " +
        "Supports conditional breakpoints and hit count conditions. " +
        "Returns the breakpoint ID for later removal.")]
    public string BreakpointSet(
        [Description("Path to the source file (absolute or relative to the debugged program)")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Optional column number (1-based)")] int? column = null,
        [Description("Optional C# expression that must be true for the breakpoint to trigger")] string? condition = null,
        [Description("Optional hit count condition (e.g. '>=5', '==3', '%2')")] string? hitCondition = null)
    {
        if (_session.CurrentState == DebugSession.State.NotStarted)
            throw new InvalidOperationException("No debug session. Use debug_launch or debug_attach first.");

        var entries = _session.SetBreakpoints(filePath, (line, column, condition, hitCondition));
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
            hint = entry.Verified
                ? null
                : "The breakpoint may be on a line that doesn't map to any IL instruction (e.g. a blank line or comment). Check the source line number."
        });
    }

    [McpServerTool, Description("Remove a breakpoint by its ID (returned from breakpoint_set).")]
    public string BreakpointRemove(
        [Description("The breakpoint ID to remove")] int id)
    {
        var removed = _session.RemoveBreakpoint(id);

        return JsonSerializer.Serialize(new
        {
            id,
            removed,
            message = removed ? "Breakpoint removed." : $"Breakpoint {id} not found."
        });
    }

    [McpServerTool, Description("List all currently set breakpoints with their status.")]
    public string BreakpointList()
    {
        var bps = _session.GetAllBreakpoints();

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
                bp.HitCondition
            })
        });
    }
}
