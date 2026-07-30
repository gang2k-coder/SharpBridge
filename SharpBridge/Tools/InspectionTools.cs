using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpBridge.Infrastructure.Attributes;
using SharpBridge.Services;
using SharpBridge.State;

namespace SharpBridge.Tools;

[McpServerToolType]
public class InspectionTools(DebugSessionManager manager)
{
    private readonly DebugSessionManager _manager = manager;

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("List all threads in the debugged process. " +
        "Requires the debugger to be in Stopped state. " +
        "Each thread has an ID you can use with stacktrace_get. " +
        "The thread that triggered the current stop is marked with isActive=true.")]
    public string ThreadsList(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var threads = session.GetThreads();

        return JsonSerializer.Serialize(new
        {
            count = threads.Count,
            threads = threads.Select(t => new
            {
                t.Id,
                t.Name,
                t.IsActive,
                hint = t.IsActive ? "This thread triggered the current stop." : null
            })
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Get the call stack for a specific thread. " +
        "Returns source file locations with line numbers. " +
        "Use threads_list first to get thread IDs. Use variables_get with a frame ID to inspect variables.")]
    public string StacktraceGet(
        [Description("Thread ID from threads_list")] int threadId,
        [Description("First frame to return (0 = top of stack)")] int startFrame = 0,
        [Description("Maximum number of frames to return")] int? levels = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var frames = session.GetStackTrace(threadId, startFrame, levels);

        return JsonSerializer.Serialize(new
        {
            threadId,
            count = frames.Count,
            frames = frames.Select((f, i) => new
            {
                id = f.Id,
                name = f.Name,
                source = f.Source is not null ? new
                {
                    path = f.Source,
                    line = f.Line,
                    column = f.Column,
                    endLine = f.EndLine,
                    endColumn = f.EndColumn
                } : null,
                hint = i == 0
                    ? "Top frame. Use variables_get with this frameId to inspect locals."
                    : null
            })
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Get variables for a stack frame. " +
        "Set depth=1 to auto-expand children (list elements, object fields) — saves round-trips. " +
        "Use 'expand' to limit expansion to specific variable names, avoiding token waste. " +
        "Use variables_expand for deeper drill-down on individual references.")]
    public string VariablesGet(
        [Description("Frame ID from stacktrace_get response")] int frameId,
        [Description("Which scope to get: 'locals', 'arguments', or 'all' (default)")] string scope = "all",
        [Description("Auto-expand depth: 0=summary only, 1=show children, 2+=recurse")] int depth = 0,
        [Description("Only expand variables with these names (null/empty = expand all at depth)")] string[]? expand = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var expandSet = expand is { Length: > 0 } ? new HashSet<string>(expand) : null;
        var variables = session.GetVariablesForFrame(frameId, scope, depth, expandSet);

        return FormatVariables(variables, frameId);
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Expand a variable to see its children. " +
        "Use the variablesReference from a previous variables_get or variables_expand call. " +
        "This shows fields, properties, array elements, or DebuggerTypeProxy views.")]
    public string VariablesExpand(
        [Description("Variables reference from a previous variables_get or variables_expand call")] int variablesReference,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var variables = session.ExpandVariables(variablesReference);

        return FormatVariables(variables, variablesReference);
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Evaluate a C# expression in the context of the current stack frame. " +
        "Can access local variables, fields, properties, and call methods. " +
        "Returns the result as a string, its type, and a variablesReference for further inspection if the result is complex.")]
    public async Task<string> Evaluate(
        [Description("C# expression to evaluate (e.g. 'x + 1', 'myList.Count', 'name.Length')")] string expression,
        [Description("Frame ID from stacktrace_get. Defaults to the topmost frame (frame 0).")] int? frameId = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var result = await session.EvaluateAsync(expression, frameId);

        return JsonSerializer.Serialize(new
        {
            expression,
            result = result.Result,
            type = result.Type,
            variablesReference = result.VariablesReference,
            hint = result.VariablesReference > 0
                ? "Result is a complex object. Use variables_expand to inspect its members."
                : null
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Get details about the current exception, if the debugger stopped " +
        "due to an unhandled or caught exception. Returns type, message, stack trace, and formatted description.")]
    public string ExceptionInfo(
        [Description("Thread ID. Uses current thread if omitted.")] int? threadId = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var ex = session.GetExceptionInfo(threadId);

        if (ex is null)
            return JsonSerializer.Serialize(new
            {
                hasException = false,
                message = "No exception on the current thread."
            });

        return JsonSerializer.Serialize(new
        {
            hasException = true,
            exceptionId = ex.ExceptionId,
            description = ex.Description,
            breakMode = ex.BreakMode,
            details = new
            {
                message = ex.Message,
                typeName = ex.TypeName,
                fullTypeName = ex.FullTypeName,
                stackTrace = ex.StackTrace,
                formattedDescription = ex.FormattedDescription
            }
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Attaching, SessionState.Stopped, SessionState.Running)]
    [Description("Configure which exceptions cause the debugger to break. " +
        "Use action='list' to see available exception filters from the debug adapter. " +
        "Use action='set' with a list of filter IDs to enable them (empty array = break on no exceptions).")]
    public string ExceptionBreakpoints(
        [Description("'list' to see available filters, 'set' to configure")] string action = "list",
        [Description("Filter IDs to enable (e.g. ['all', 'user-unhandled']). Only for action='set'.")] string[]? filters = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);

        if (action == "list")
        {
            var availableFilters = session.GetExceptionBreakpointFilters();
            if (availableFilters is null || availableFilters.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    count = 0,
                    message = "No exception breakpoint filters available from the debug adapter."
                });

            return JsonSerializer.Serialize(new
            {
                count = availableFilters.Count,
                filters = availableFilters.Select(f => new
                {
                    id = f.Filter,
                    label = f.Label,
                    description = f.Description,
                    defaultEnabled = f.Default
                }),
                hint = "Use exception_breakpoints(action='set', filters=['...']) to enable the desired filters."
            });
        }

        if (action == "set")
        {
            var enabledFilters = filters ?? [];
            session.SetExceptionBreakpoints(enabledFilters);

            return JsonSerializer.Serialize(new
            {
                status = "configured",
                enabledFilters = enabledFilters,
                note = enabledFilters.Length == 0
                    ? "Exception breakpoints disabled. The debugger will NOT stop on exceptions."
                    : $"Exception breakpoints enabled: [{string.Join(", ", enabledFilters)}]. The debugger will stop on matching exceptions."
            });
        }

        throw new ArgumentException($"Unknown action '{action}'. Use 'list' or 'set'.");
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped)]
    [Description("Manually capture variables at the current stop point. Must be in Stopped state.")]
    public string CaptureState(
        [Description("Which scope: 'locals', 'arguments', or 'all' (default)")] string scope = "all",
        [Description("Variable expansion depth: 0=summary only, 1=show children, 2+=recurse")] int depth = 0,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var snapshot = session.CaptureState(scope, depth);

        return JsonSerializer.Serialize(new
        {
            index = snapshot.Index,
            reason = snapshot.Reason,
            threadId = snapshot.ThreadId,
            source = snapshot.FilePath is not null ? new { path = snapshot.FilePath, line = snapshot.Line } : null,
            timestamp = snapshot.Timestamp,
            variables = snapshot.Variables.Select(FormatVariable)
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped, SessionState.Running)]
    [Description("Get all accumulated capture snapshots. " +
        "Snapshots come from: (1) breakpoints with action='capture' that fire during debug_continue, " +
        "and (2) manual capture_state calls. Each snapshot contains captured variables, source location, " +
        "timestamp, and an incrementing index. Use after debug_continue with capture-action breakpoints.")]
    public string GetCaptures(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var captures = session.GetCaptures();

        return JsonSerializer.Serialize(new
        {
            count = captures.Count,
            message = captures.Count == 0
                ? "No captures recorded. Use capture_state to take snapshots, or set breakpoints with action='capture'."
                : null,
            captures = captures.Select(c => new
            {
                index = c.Index,
                reason = c.Reason,
                source = c.FilePath is not null ? new { path = c.FilePath, line = c.Line } : null,
                timestamp = c.Timestamp,
                variables = c.Variables.Select(FormatVariable)
            })
        });
    }

    [McpServerTool]
    [AllowedState(SessionState.Stopped, SessionState.Running)]
    [Description("Clear all accumulated capture snapshots. Call before starting " +
        "a new debug_continue with capture-action breakpoints to reset the capture history.")]
    public string ClearCaptures(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        session.ClearCaptures();

        return JsonSerializer.Serialize(new { status = "cleared" });
    }

    private static string FormatVariables(IReadOnlyList<VariableInfo> variables, int source)
    {
        return JsonSerializer.Serialize(new
        {
            source,
            count = variables.Count,
            variables = variables.Select(FormatVariable)
        });
    }

    private static object FormatVariable(VariableInfo v)
    {
        return new
        {
            v.Name,
            v.Value,
            v.Type,
            v.VariablesReference,
            v.EvaluateName,
            v.IndexedVariables,
            v.NamedVariables,
            expandable = v.VariablesReference > 0,
            hint = v.VariablesReference > 0 && v.Children is null
                ? $"Use variables_expand with variablesReference={v.VariablesReference} to see children."
                : null,
            children = v.Children?.Select(FormatVariable)
        };
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
