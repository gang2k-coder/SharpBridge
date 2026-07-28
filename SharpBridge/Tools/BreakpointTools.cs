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
        "Supports conditional breakpoints, hit count conditions, and capture action. " +
        "action='break' (default) stops and waits; action='capture' auto-captures " +
        "variables (per captureScope/captureDepth) and continues without stopping. " +
        "Returns the breakpoint ID for later removal.")]
    public string BreakpointSet(
        [Description("Path to the source file (absolute or relative to the debugged program)")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Optional column number (1-based)")] int? column = null,
        [Description("Optional C# expression that must be true for the breakpoint to trigger")] string? condition = null,
        [Description("Optional hit count condition (e.g. '>=5', '==3', '%2')")] string? hitCondition = null,
        [Description("'break' = stop and wait (default), 'capture' = auto-snapshot variables and continue")] string action = "break",
        [Description("Capture scope (only when action='capture'): 'locals', 'arguments', or 'all' (default)")] string captureScope = "all",
        [Description("Capture expansion depth (only when action='capture'): 0=summary, 1+=expand children")] int captureDepth = 0,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);

        var entries = session.SetBreakpoints(filePath,
            (line, column, condition, hitCondition, action, captureScope, captureDepth));
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
            captureScope = entry.CaptureScope,
            captureDepth = entry.CaptureDepth,
            hint = entry.Verified
                ? (entry.Action == "capture" ? "Capture-action: will auto-capture variables and continue." : null)
                : "The breakpoint may be on a line that doesn't map to any IL instruction (e.g. a blank line or comment). Check the source line number."
        });
    }

    [McpServerTool, Description(
        "Set a function breakpoint that breaks when a specific method is entered. " +
        "Supports multiple naming patterns for precise method targeting:\n" +
        "- 'Namespace.Class.Method' — fully qualified name (exact or suffix match)\n" +
        "- 'Class.Method' — short name (matches any namespace)\n" +
        "- 'Method' — method name only (matches any type)\n" +
        "- 'Method(int, string)' — with parameter types for overload disambiguation\n" +
        "- 'GenericClass<T>.Method' — generic type with arity\n" +
        "- 'GenericClass<T>.Method<T>(T, int)' — full generic method with parameters\n" +
        "C# type aliases (int, string, bool, long, etc.) are automatically resolved to CLR types.\n" +
        "When multiple methods match (overloads, multiple modules), all are bound simultaneously.\n" +
        "Returns the breakpoint ID for later removal with breakpoint_remove.")]
    public string FunctionBreakpointSet(
        [Description("Function name pattern. Examples:\n" +
            "- 'Calculator.Multiply' — simple class.method\n" +
            "- 'Multiply' — method name only (suffix match)\n" +
            "- 'Greeter.GetGreeting(string)' — single-param overload\n" +
            "- 'Greeter.GetGreeting(string, string)' — two-param overload\n" +
            "- 'MyApp.GenericProcessor<T>.Process(T)' — generic method with parameter")] string functionName,
        [Description("Optional C# expression that must be true for the breakpoint to trigger")] string? condition = null,
        [Description("Optional hit count condition (e.g. '>=5', '==3', '%2')")] string? hitCondition = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null)
    {
        var session = _manager.Resolve(sessionId);

        // DAP replaces all function breakpoints each call — preserve existing ones
        var all = session.GetAllBreakpoints()
            .Where(bp => bp.FunctionName is not null)
            .Select(bp => (bp.FunctionName!, bp.Condition, bp.HitCondition,
                           bp.Action, bp.CaptureScope, bp.CaptureDepth))
            .ToList();
        all.Add((functionName, condition, hitCondition, "break", null, 0));

        var entries = session.SetFunctionBreakpoints(all.ToArray());
        var entry = entries.Last(); // the one just added

        return JsonSerializer.Serialize(new
        {
            id = entry.Id,
            functionName = entry.FunctionName,
            verified = entry.Verified,
            message = entry.Message ?? (entry.Verified
                ? "Function breakpoint is set and will be hit when the method is called."
                : "Function breakpoint could not be resolved. It may match a method in a module that hasn't loaded yet."),
            condition = entry.Condition,
            hitCondition = entry.HitCondition,
            hint = entry.Verified
                ? "The breakpoint will fire whenever any matching method is called."
                : "It will bind automatically when the matching module loads."
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
                bp.FunctionName,
                bp.Verified,
                bp.Message,
                bp.Condition,
                bp.HitCondition,
                bp.Action,
                bp.CaptureScope,
                bp.CaptureDepth,
                hint = bp.Action == "capture"
                    ? "Capture-action: auto-captures variables and continues."
                    : null
            })
        });
    }
}
