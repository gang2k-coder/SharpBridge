using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Infrastructure.Attributes;
using SharpBridge.Services;
using SharpBridge.State;

namespace SharpBridge.Tools;

[McpServerToolType]
public class BreakpointTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped, SessionState.Running)]
    [Description("Set a breakpoint at a specific file and line. " +
        "Multiple breakpoints in the same file ACCUMULATE — each call adds a " +
        "breakpoint and never replaces existing ones in that file. " +
        "Supports conditional breakpoints, hit count conditions, and capture action. " +
        "action='break' (default) stops and waits; action='capture' auto-captures " +
        "variables (per captureScope/captureDepth) and continues without stopping. " +
        "NOTE: setting breakpoints re-sends the file's breakpoints to the adapter, which " +
        "refreshes all breakpoint IDs in that file — always use the IDs from the latest " +
        "response or breakpoint_list. " +
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
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);

        // DAP replaces all source breakpoints per file on each call — preserve
        // existing breakpoints in the file (same pattern as function bps).
        // Re-set with the CURRENT (possibly adapter-adjusted) line and keep the
        // original action/conditions so nothing is silently dropped.
        var existing = session.GetAllBreakpoints()
            .Where(bp => bp.FunctionName is null && NormalizePath(bp.FilePath) == NormalizePath(filePath))
            .Select(bp => (bp.Line, bp.Column, bp.Condition, bp.HitCondition,
                           bp.Action, bp.CaptureScope, bp.CaptureDepth))
            .ToList();
        existing.Add((line, column, condition, hitCondition, action, captureScope, captureDepth));

        var entries = session.SetBreakpoints(filePath, existing.ToArray());
        var entry = entries.Last(); // the one just added

        var status = DebugSession.BreakpointStatus(entry);
        return JsonSerializer.Serialize(new
        {
            id = entry.Id,
            filePath = entry.FilePath,
            line = entry.Line,
            column = entry.Column,
            verified = entry.Verified,
            status,
            message = status switch
            {
                "failed" => entry.Message ?? "Breakpoint could not be verified.",
                "pending" => "Breakpoint is pending: the target module is not loaded yet.",
                _ => entry.Message ?? "Breakpoint is set and will be hit."
            },
            condition = entry.Condition,
            hitCondition = entry.HitCondition,
            action = entry.Action,
            captureScope = entry.CaptureScope,
            captureDepth = entry.CaptureDepth,
            fileBreakpointCount = entries.Count,
            hint = status switch
            {
                "pending" => "Breakpoint is pending: it will bind automatically when the module loads (verified will flip to true).",
                "failed" => "Breakpoint could not be bound. Check that the source path matches the debuggee's PDB and the line is executable.",
                _ => entry.Action == "capture"
                    ? "Capture-action: will auto-capture variables and continue."
                    : null
            }
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped, SessionState.Running)]
    [Description(
        "Set a function breakpoint that breaks when a specific method is entered. " +
        "Supports multiple naming patterns for precise method targeting:\n" +
        "- 'Namespace.Class.Method' — fully qualified name (exact or suffix match)\n" +
        "- 'Class.Method' — short name (matches any namespace)\n" +
        "- 'Method' — method name only (matches any type)\n" +
        "- 'Method(int, string)' — with parameter types for overload disambiguation\n" +
        "- 'GenericClass<T>.Method' — generic type with arity\n" +
        "- 'GenericClass<T>.Method<T>(T, int)' — full generic method with parameters\n" +
        "C# type aliases (int, string, bool, long, etc.) are automatically resolved to CLR types.\n" +
        "When multiple methods match (overloads, multiple modules), all are bound simultaneously — omit the parameter list to bind all overloads of a name.\n" +
        "Method name matches EXACTLY (case-sensitive, no wildcards); type segment matches exactly or by '.TypeName' suffix.\n" +
        "Requires the target module's PDB; binds at method entry. Set before the module loads → reported pending, binds automatically on load.\n" +
        "LIMITATION: local functions and lambdas cannot be targeted — the compiler mangles them (e.g. <<Main>$>g__SignalLoopEnd|0_0) and '<>'/'|' are reserved; use regular methods.\n" +
        "NOTE: each call re-sends ALL function breakpoints, refreshing their IDs — use the returned ID.\n" +
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
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);

        // DAP replaces all function breakpoints each call — preserve existing ones
        var all = session.GetAllBreakpoints()
            .Where(bp => bp.FunctionName is not null)
            .Select(bp => (bp.FunctionName!, bp.Condition, bp.HitCondition,
                           bp.Action, bp.CaptureScope, bp.CaptureDepth))
            .ToList();
        all.Add((functionName, condition, hitCondition, "break", null, 0));

        var entries = session.SetFunctionBreakpoints(all.ToArray());
        var entry = entries.Last(); // the one just added

        var status = DebugSession.BreakpointStatus(entry);
        return JsonSerializer.Serialize(new
        {
            id = entry.Id,
            functionName = entry.FunctionName,
            verified = entry.Verified,
            status,
            message = status switch
            {
                "failed" => entry.Message ?? "Function breakpoint could not be resolved.",
                "pending" => "Function breakpoint is pending: the target module is not loaded yet.",
                _ => entry.Message ?? "Function breakpoint is set and will be hit when the method is called."
            },
            condition = entry.Condition,
            hitCondition = entry.HitCondition,
            hint = status switch
            {
                "pending" => "It will bind automatically when the matching module loads.",
                "failed" => "No matching method found in any loaded module. Note: local functions and lambdas compile to mangled names (e.g. <<Main>$>g__Name|0_0) and cannot be targeted.",
                _ => "The breakpoint will fire whenever any matching method is called."
            }
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped, SessionState.Running)]
    [Description("Remove a breakpoint by its ID (returned from breakpoint_set). " +
        "Removing re-sends the remaining breakpoints in the same file (or all function " +
        "breakpoints for a function bp), so their IDs refresh — use breakpoint_list " +
        "for current IDs.")]
    public string BreakpointRemove(
        [Description("The breakpoint ID to remove")] int id,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var removed = session.RemoveBreakpoint(id);

        return JsonSerializer.Serialize(new
        {
            id,
            removed,
            message = removed ? "Breakpoint removed." : $"Breakpoint {id} not found."
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped, SessionState.Running)]
    [Description("List all currently set breakpoints with their status, conditions, and capture configuration.")]
    public string BreakpointList(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var bps = session.GetAllBreakpoints();

        return JsonSerializer.Serialize(new
        {
            count = bps.Count,
            breakpoints = bps.Select(bp => new
            {
                id = bp.Id,
                filePath = bp.FilePath,
                line = bp.Line,
                column = bp.Column,
                functionName = bp.FunctionName,
                verified = bp.Verified,
                status = DebugSession.BreakpointStatus(bp),
                message = bp.Message,
                condition = bp.Condition,
                hitCondition = bp.HitCondition,
                action = bp.Action,
                captureScope = bp.CaptureScope,
                captureDepth = bp.CaptureDepth,
                hint = bp.Action == "capture"
                    ? "Capture-action: auto-captures variables and continues."
                    : bp.Verified
                        ? null
                        : bp.IsPending
                            ? "Pending: binds when the module loads. May remain pending forever if the module never loads."
                            : "Not verified: check the source path/line against the debuggee's PDB."
            })
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

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }
}
