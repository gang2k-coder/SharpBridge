using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [AllowedState(SessionState.Running, SessionState.Stopped)]
    [Description("List all modules (assemblies) loaded into the debugged process. " +
        "Modules are reported by SharpDbg as they load (LoadModule callbacks); after attach the list is populated once the program runs — the CLR is frozen during Attaching and no modules are known until the first debug_continue. " +
        "Use this to check whether a target assembly has loaded (e.g. when a breakpoint stays pending, or to verify which path an assembly was loaded from). " +
        "Each module has id (module path), name (file name), and path.")]
    public string ModulesList(
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        var session = ResolveSession(processId, processName);
        var modules = session.GetModules();

        return JsonSerializer.Serialize(new
        {
            count = modules.Count,
            modules = modules.Select(m => new
            {
                id = m.Id,
                name = m.Name,
                path = m.Path
            })
        });
    }

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
        session.ObserveStopState();
        var threads = session.GetThreads();

        return JsonSerializer.Serialize(new
        {
            count = threads.Count,
            threads = threads.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                isActive = t.IsActive,
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
        session.ObserveStopState();
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
        session.ObserveStopState();
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
        session.ObserveStopState();
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
        session.ObserveStopState();
        var result = await session.EvaluateAsync(expression, frameId);

        return JsonSerializer.Serialize(new
        {
            expression,
            result = result.Result,
            type = result.Type,
            isError = result.IsError,
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
        session.ObserveStopState();
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
        session.ObserveStopState();
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

    [McpServerTool]
    [AllowedState(SessionState.Stopped, SessionState.Running)]
    [Description("Get capture snapshots with v2 shaping: filtering, pagination, " +
        "JSON-field extraction, spill-to-file, deterministic aggregation, and paste-ready " +
        "markdown export. format='full' returns the v1 get_captures payload (plus " +
        "apiVersion/format keys); 'summary' (default) targets a <8 KB response; 'compact' " +
        "drops variables; 'markdown' renders flat tables for issue reports.")]
    public string GetCapturesV2(
        [Description("Output shape: 'summary' (default), 'compact', 'full', or 'markdown'")] string format = "summary",
        [Description("Skip this many captures after filtering")] int offset = 0,
        [Description("Maximum captures to return (default 20, max 100)")] int limit = 20,
        [Description("Only captures whose 1-based capture index is in this list")] int[]? captureIndex = null,
        [Description("Only captures whose source path contains this substring")] string? sourcePathContains = null,
        [Description("Only captures at exactly this source line")] int? sourceLine = null,
        [Description("Compute aggregate stats over these variable/extract-pick fields: " +
            "bySourceLine (always), counts.<field>_null, and distinctValues for fields with <=5 distinct values. " +
            "Omit for aggregate=null.")] string[]? aggregateFields = null,
        [Description("Extract fields from JSON-string variables, e.g. [{variable: 'payloadJson', type: 'json', pick: ['items[0].id']}]")] ExtractRule[]? extract = null,
        [Description("Write the full capture payload to temp file(s) and return path(s) in spillFile; large inline values become placeholders")] bool spillToFile = false,
        [Description("Per-variable byte threshold: larger values become '<spilled: N KB>' inline (default 8192)")] int? spillThresholdBytes = null,
        [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
        [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
    {
        if (format is not ("summary" or "full" or "compact" or "markdown"))
            throw new ArgumentException(
                $"Unknown format '{format}' — use 'summary', 'full', 'compact', or 'markdown'.");
        if (offset < 0) throw new ArgumentException("offset must be >= 0.");
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var threshold = spillThresholdBytes ?? 8192;
        if (threshold < 0) throw new ArgumentException("spillThresholdBytes must be >= 0.");

        var session = ResolveSession(processId, processName);
        var captures = session.GetCaptures();

        // === Filters (AND together, before pagination) ===
        IEnumerable<CaptureSnapshot> filtered = captures;
        if (captureIndex is { Length: > 0 })
        {
            var set = captureIndex.ToHashSet();
            filtered = filtered.Where(c => set.Contains(c.Index));
        }
        if (!string.IsNullOrEmpty(sourcePathContains))
            filtered = filtered.Where(c =>
                c.FilePath is not null &&
                c.FilePath.Contains(sourcePathContains, StringComparison.OrdinalIgnoreCase));
        if (sourceLine.HasValue)
            filtered = filtered.Where(c => c.Line == sourceLine.Value);
        var filteredList = filtered.ToList();
        var page = filteredList.Skip(offset).Take(effectiveLimit).ToList();

        // === format=full: v1 payload + apiVersion/format at the top level ===
        if (format == "full")
        {
            return JsonSerializer.Serialize(new
            {
                apiVersion = 2,
                format = "full",
                count = page.Count,
                message = page.Count == 0
                    ? "No captures recorded. Use capture_state to take snapshots, or set breakpoints with action='capture'."
                    : null,
                captures = page.Select(c => new
                {
                    index = c.Index,
                    reason = c.Reason,
                    source = c.FilePath is not null ? new { path = c.FilePath, line = c.Line } : null,
                    timestamp = c.Timestamp,
                    variables = c.Variables.Select(FormatVariable)
                })
            });
        }

        // === Extraction pass (original tree, before any rendering) ===
        var extractedByCapture = new Dictionary<int, List<ExtractedResult>>();
        var jsonReplacedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (extract is { Length: > 0 })
        {
            foreach (var c in page)
            {
                var results = new List<ExtractedResult>();
                foreach (var rule in extract)
                {
                    var varName = rule.Variable ?? "";
                    var node = FindVariable(c.Variables, varName);
                    if (node is null)
                    {
                        results.Add(new ExtractedResult(varName, null, "variable-not-found"));
                        continue;
                    }
                    if (rule.Type != "json")
                    {
                        results.Add(new ExtractedResult(varName, null, $"unsupported-type:{rule.Type}"));
                        continue;
                    }
                    if (!TryParseJson(node.Value, out var doc))
                    {
                        results.Add(new ExtractedResult(varName, null, "not-json"));
                        continue;
                    }
                    using (doc)
                    {
                        var picked = new JsonObject();
                        foreach (var path in rule.Pick ?? [])
                            picked[path] = TryPick(doc.RootElement, path, out var el)
                                ? JsonNode.Parse(el.GetRawText())
                                : null;
                        results.Add(new ExtractedResult(varName, picked, null));
                        jsonReplacedPaths.Add(varName);
                    }
                }
                if (results.Count > 0)
                    extractedByCapture[c.Index] = results;
            }
        }

        // === Deterministic aggregate (computed once, over the FILTERED set) ===
        var aggregate = BuildAggregate(filteredList, aggregateFields ?? [], extractedByCapture);

        // === compact: summary minus variables (and expressions) ===
        if (format == "compact")
        {
            return JsonSerializer.Serialize(new
            {
                apiVersion = 2,
                format = "compact",
                session = new { processId = session.ProcessId, processName = session.ProcessName },
                totalCaptures = filteredList.Count,
                returned = page.Count,
                offset,
                truncated = false,
                captures = page.Select(c => new
                {
                    index = c.Index,
                    timestamp = c.Timestamp,
                    reason = c.Reason,
                    breakpointId = c.BreakpointId,
                    source = c.FilePath is not null
                        ? new { path = c.FilePath, file = Path.GetFileName(c.FilePath), line = c.Line }
                        : null,
                    extracted = RenderExtracted(extractedByCapture.TryGetValue(c.Index, out var ex) ? ex : null),
                    spillFile = (string?)null
                }),
                aggregate
            });
        }

        // === markdown: flat tables, no smart grouping ===
        if (format == "markdown")
        {
            return RenderMarkdown(page, filteredList.Count,
                session.ProcessId, session.ProcessName,
                threshold, jsonReplacedPaths, extractedByCapture, spillToFile, aggregate);
        }

        // === Summary rendering with deterministic budget loop ===
        var maxDepth = page.Select(c => MaxTreeDepth(c.Variables)).DefaultIfEmpty(0).Max();
        string payload;
        var depthCap = maxDepth;
        var truncated = false;
        while (true)
        {
            var rendered = page.Select(c => RenderSummaryCapture(
                c, depthCap, threshold, extractedByCapture, jsonReplacedPaths, spillToFile)).ToList();
            payload = JsonSerializer.Serialize(new
            {
                apiVersion = 2,
                format = "summary",
                session = new { processId = session.ProcessId, processName = session.ProcessName },
                totalCaptures = filteredList.Count,
                returned = page.Count,
                offset,
                truncated,
                captures = rendered,
                aggregate
            });
            if (payload.Length <= 8 * 1024 || depthCap == 0)
                break;
            depthCap--;
            truncated = true;
        }

        return payload;
    }

    private object RenderSummaryCapture(
        CaptureSnapshot c, int depthCap, int threshold,
        Dictionary<int, List<ExtractedResult>> extractedByCapture,
        HashSet<string> jsonReplacedPaths, bool spillToFile)
    {
        var rawVariables = c.Variables.ToList();

        string? spillFile = spillToFile ? WriteSpillFile(c) : null;

        return new
        {
            index = c.Index,
            timestamp = c.Timestamp,
            reason = c.Reason,
            breakpointId = c.BreakpointId,
            source = c.FilePath is not null
                ? new { path = c.FilePath, file = Path.GetFileName(c.FilePath), line = c.Line }
                : null,
            variables = rawVariables.Select(v => RenderVariableInline(v, v.Name, depthCap, threshold, jsonReplacedPaths)),
            extracted = RenderExtracted(extractedByCapture.TryGetValue(c.Index, out var ex) ? ex : null),
            spillFile
        };
    }

    /// <summary>Extract result for one rule on one capture. Wire format stays
    /// camelCase via RenderExtracted (anonymous projection).</summary>
    private sealed record ExtractedResult(string Variable, JsonObject? Picked, string? Error);

    private static object? RenderExtracted(List<ExtractedResult>? list)
        => list?.Select(r => new { variable = r.Variable, picked = r.Picked, error = r.Error });

    private static string WriteSpillFile(CaptureSnapshot c)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"sharpbridge-capture-{c.Index}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            index = c.Index,
            reason = c.Reason,
            breakpointId = c.BreakpointId,
            source = c.FilePath is not null ? new { path = c.FilePath, line = c.Line } : null,
            timestamp = c.Timestamp,
            variables = c.Variables.Select(FormatVariable)
        }));
        return path;
    }

    // ===================================================================
    // Deterministic aggregate (spec 5.4): computed ONLY from fields the
    // request names explicitly — input -> output is fixed and testable.
    // ===================================================================

    private static object? BuildAggregate(
        IReadOnlyList<CaptureSnapshot> captures, string[] aggregateFields,
        Dictionary<int, List<ExtractedResult>> extractedByCapture)
    {
        if (aggregateFields.Length == 0)
            return null;

        // bySourceLine is always computed (post-filter captures).
        var bySourceLine = captures
            .Where(c => c.FilePath is not null)
            .GroupBy(c => $"{Path.GetFileName(c.FilePath!)}:{c.Line}")
            .ToDictionary(g => g.Key, g => g.Count());

        var counts = new Dictionary<string, int>();
        var distinctValues = new Dictionary<string, List<string>>();
        foreach (var field in aggregateFields)
        {
            var values = new List<string>(captures.Count);
            var nullCount = 0;
            foreach (var c in captures)
            {
                var v = ResolveFieldValue(c, field, extractedByCapture)?.Trim();
                if (string.IsNullOrEmpty(v) || v == "null")
                    nullCount++;
                values.Add(v ?? "null");
            }
            counts[$"{field}_null"] = nullCount;

            // distinctValues: only low-cardinality fields whose values are
            // display-sized (<=256 chars — a 61 KB distinct value would blow
            // the G1 token budget and is useless to an agent).
            var distinct = values.Distinct().ToList();
            if (distinct.Count <= 5 && distinct.All(s => s.Length <= 256))
                distinctValues[field] = distinct.Take(50).ToList();
        }

        return new { bySourceLine, counts, distinctValues };
    }

    /// <summary>Resolve an aggregate field: top-level variable first, then
    /// extract pick paths. No deep search. String values are normalized to
    /// display form (SharpDbg C#-style outer quotes stripped).</summary>
    private static string? ResolveFieldValue(
        CaptureSnapshot c, string field,
        Dictionary<int, List<ExtractedResult>> extractedByCapture)
    {
        var v = c.Variables.FirstOrDefault(x => x.Name == field);
        if (v is not null)
            return NormalizeDisplayValue(v.Value);
        if (extractedByCapture.TryGetValue(c.Index, out var results))
            foreach (var r in results)
                if (r.Picked is not null && r.Picked.TryGetPropertyValue(field, out var node))
                    return NodeToDisplay(node);
        return null;
    }

    private static string? NormalizeDisplayValue(string? raw)
    {
        if (raw is null)
            return null;
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = UnescapeSharpDbgString(t[1..^1]);
        return t;
    }

    private static string NodeToDisplay(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
                return s;
            return value.ToJsonString();
        }
        return node.ToJsonString();
    }

    // ===================================================================
    // Markdown: flat tables only — no smart grouping (deterministic).
    // ===================================================================

    private static string RenderMarkdown(
        List<CaptureSnapshot> page, int total,
        int? processId, string? processName,
        int threshold, HashSet<string> jsonReplacedPaths,
        Dictionary<int, List<ExtractedResult>> extractedByCapture,
        bool spillToFile, object? aggregate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## SharpBridge captures — {processName ?? "unknown"} (pid {processId})");
        sb.AppendLine();
        sb.AppendLine($"**Total:** {total} | **Shown:** {page.Count}");

        var depthCap = page.Select(c => MaxTreeDepth(c.Variables)).DefaultIfEmpty(0).Max();
        foreach (var c in page)
        {
            var srcName = c.FilePath is not null
                ? $"{Path.GetFileName(c.FilePath)}:{c.Line}"
                : "unknown location";
            sb.AppendLine();
            sb.AppendLine($"### Capture #{c.Index} — {srcName}");
            sb.AppendLine();
            sb.AppendLine("| variable | value |");
            sb.AppendLine("|---|---|");
            foreach (var (name, value) in FlattenVariables(c.Variables, depthCap, threshold, jsonReplacedPaths))
                sb.AppendLine($"| {EscapeMarkdownCell(name)} | {EscapeMarkdownCell(value)} |");

            if (extractedByCapture.TryGetValue(c.Index, out var ex))
            {
                sb.AppendLine();
                sb.AppendLine("**Extracted:**");
                sb.AppendLine();
                sb.AppendLine("| path | value |");
                sb.AppendLine("|---|---|");
                foreach (var r in ex)
                {
                    if (r.Picked is not null)
                    {
                        foreach (var (path, node) in r.Picked)
                            sb.AppendLine($"| {EscapeMarkdownCell(path)} | {EscapeMarkdownCell(NodeToDisplay(node))} |");
                    }
                    else
                    {
                        sb.AppendLine($"| {EscapeMarkdownCell(r.Variable)} | error: {EscapeMarkdownCell(r.Error ?? "")} |");
                    }
                }
            }

            if (spillToFile)
            {
                var spillPath = WriteSpillFile(c);
                sb.AppendLine();
                sb.AppendLine($"**Spill file:** {spillPath}");
            }
        }

        if (aggregate is not null)
        {
            sb.AppendLine();
            sb.AppendLine("**Aggregate:**");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(aggregate));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static List<(string Name, string Value)> FlattenVariables(
        IReadOnlyList<VariableInfo> vars, int depthCap, int threshold,
        HashSet<string> jsonReplacedPaths)
    {
        var rows = new List<(string, string)>();
        void Walk(IReadOnlyList<VariableInfo> level, string prefix, int remaining)
        {
            foreach (var v in level)
            {
                var path = prefix.Length == 0 ? v.Name : $"{prefix}.{v.Name}";
                var value = v.Value;
                if (jsonReplacedPaths.Contains(path))
                    value = $"<json: {FormatKb(value)} KB, see extracted>";
                else if (value.Length > 0 && Encoding.UTF8.GetByteCount(value) > threshold)
                    value = $"<spilled: {FormatKb(value)} KB>";
                rows.Add((path, value));
                if (remaining > 0 && v.Children is { Count: > 0 })
                    Walk(v.Children, path, remaining - 1);
            }
        }
        Walk(vars, "", depthCap);
        return rows;
    }

    private static string EscapeMarkdownCell(string s)
        => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private object RenderVariableInline(
        VariableInfo v, string path, int remainingDepth, int threshold,
        HashSet<string> jsonReplacedPaths)
    {
        string value = v.Value;
        if (jsonReplacedPaths.Contains(path))
            value = $"<json: {FormatKb(value)} KB, see extracted>";
        else if (value.Length > 0 && Encoding.UTF8.GetByteCount(value) > threshold)
            value = $"<spilled: {FormatKb(value)} KB>";

        return new
        {
            name = v.Name,
            value,
            type = v.Type,
            variablesReference = v.VariablesReference,
            evaluateName = v.EvaluateName,
            indexedVariables = v.IndexedVariables,
            namedVariables = v.NamedVariables,
            expandable = v.VariablesReference > 0,
            hint = v.VariablesReference > 0 && v.Children is null
                ? $"Use variables_expand with variablesReference={v.VariablesReference} to see children."
                : null,
            children = remainingDepth > 0
                ? v.Children?.Select(ch => RenderVariableInline(
                    ch, path.Length == 0 ? ch.Name : $"{path}.{ch.Name}",
                    remainingDepth - 1, threshold, jsonReplacedPaths))
                : null
        };
    }

    private static int MaxTreeDepth(IReadOnlyList<VariableInfo> vars)
    {
        var max = 0;
        foreach (var v in vars)
        {
            if (v.Children is { Count: > 0 })
                max = Math.Max(max, 1 + MaxTreeDepth(v.Children));
        }
        return max;
    }

    private static VariableInfo? FindVariable(IReadOnlyList<VariableInfo> variables, string dottedPath)
    {
        VariableInfo? current = null;
        IReadOnlyList<VariableInfo> level = variables;
        foreach (var seg in dottedPath.Split('.'))
        {
            current = level.FirstOrDefault(v => v.Name == seg);
            if (current is null) return null;
            level = current.Children ?? [];
        }
        return current;
    }

    private static bool TryParseJson(string value, out JsonDocument doc)
    {
        doc = null!;
        var trimmed = value.Trim();
        // SharpDbg renders string values C#-style: surrounding quotes and
        // ALL inner quotes backslash-escaped. Strip the outer quotes and
        // unescape before attempting a JSON parse.
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        trimmed = UnescapeSharpDbgString(trimmed);
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            doc = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Undo SharpDbg's C#-style escaping: \" -> ", \\ -> \, and the
    /// standard \n \r \t escapes.</summary>
    private static string UnescapeSharpDbgString(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                switch (next)
                {
                    case '"': sb.Append('"'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    default: sb.Append(ch); continue;
                }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Resolve a dot/index path ("items[0].id") against a parsed JSON document.</summary>
    private static bool TryPick(JsonElement root, string path, out JsonElement result)
    {
        result = default;
        JsonElement current = root;
        foreach (var token in TokenizePickPath(path))
        {
            if (token.StartsWith('[') && token.EndsWith(']') && token.Length > 2)
            {
                if (!int.TryParse(token.AsSpan(1, token.Length - 2), out var idx))
                    return false;
                if (current.ValueKind != JsonValueKind.Array || idx < 0 || idx >= current.GetArrayLength())
                    return false;
                current = current[idx];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out var next))
                    return false;
                current = next;
            }
        }
        result = current;
        return true;
    }

    private static IEnumerable<string> TokenizePickPath(string path)
    {
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.') { i++; continue; }
            if (path[i] == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0) throw new ArgumentException($"Invalid pick path: {path}");
                yield return path[i..(end + 1)];
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            yield return path[start..i];
        }
    }

    private static string FormatKb(string value)
        => Math.Max(1, (int)Math.Round(Encoding.UTF8.GetByteCount(value) / 1024.0)).ToString();

    /// <summary>Inline extract rule for get_captures_v2.</summary>
    public sealed class ExtractRule
    {
        public string? Variable { get; set; }
        public string Type { get; set; } = "json";
        public string[]? Pick { get; set; }
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
            name = v.Name,
            value = v.Value,
            type = v.Type,
            variablesReference = v.VariablesReference,
            evaluateName = v.EvaluateName,
            indexedVariables = v.IndexedVariables,
            namedVariables = v.NamedVariables,
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
