using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpBridge.Infrastructure.Attributes;
using SharpBridge.Services;
using SharpBridge.State;

namespace SharpBridge.Infrastructure.Filters;

public static class CallToolFilters
{
    /// <summary>
    /// MCP request filter that enforces [AllowedState] constraints on tools.
    /// Tools without the attribute pass through unconditionally.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> SessionStateFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? "";

            // Look up the tool
            var toolCollection = context.Server.ServerOptions.ToolCollection;
            if (toolCollection is null || !toolCollection.TryGetPrimitive(toolName, out var tool))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Unknown tool: {toolName}" }],
                    IsError = true
                };
            }

            // Check for [AllowedState] — if absent, pass through
            var attr = tool.Metadata.OfType<AllowedStateAttribute>().FirstOrDefault();
            if (attr is null)
            {
                return await next(context, cancellationToken);
            }

            // Resolve the target session from tool arguments
            var sessionManager = context.Services?.GetService<DebugSessionManager>();
            if (sessionManager is null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Internal error: DebugSessionManager not available." }],
                    IsError = true
                };
            }

            var args = context.Params?.Arguments;
            DebugSession session;

            try
            {
                session = ResolveSession(sessionManager, args);
            }
            catch (InvalidOperationException ex)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = ex.Message }],
                    IsError = true
                };
            }

            // Enforce state
            if (!attr.AllowedStates.Contains(session.CurrentState))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Tool '{toolName}' requires session state [{string.Join(", ", attr.AllowedStates)}] " +
                               $"but current state is '{session.CurrentState}'."
                    }],
                    IsError = true
                };
            }

            return await next(context, cancellationToken);
        };
    }

    private static DebugSession ResolveSession(
        DebugSessionManager manager,
        IDictionary<string, JsonElement>? args)
    {
        // Try processId first
        if (args is not null && args.TryGetValue("processId", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number)
        {
            return manager.Resolve(pidEl.GetInt32());
        }

        // Try processName
        if (args is not null && args.TryGetValue("processName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            var name = nameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                return manager.Resolve(name!);
        }

        // Fall back to selected session
        return manager.Resolve(processId: null);
    }
}
