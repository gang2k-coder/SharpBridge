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
            try
            {
                return await ExecuteAsync(context, cancellationToken, next);
            }
            catch (OperationCanceledException)
            {
                // Cancellation must propagate — the caller asked to stop.
                throw;
            }
            catch (Exception ex)
            {
                // The MCP SDK swallows tool exceptions into a generic
                // "An error occurred invoking 'X'." message that carries no
                // diagnostics. Catching here (inside the SDK's wrapper) lets
                // the real message reach the agent, which is essential for
                // self-correcting long-running sessions.
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = ex.Message }],
                    IsError = true
                };
            }
        };
    }

    private static async Task<CallToolResult> ExecuteAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
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

            // Serialize the whole tool invocation under the session gate so
            // concurrent MCP calls cannot interleave DAP requests or race the
            // state machine. The gate also fails fast with a clear error when
            // the session was cleaned up underneath a caller.
            try
            {
                return await session.WithSessionLockAsync<CallToolResult>(
                    () => next(context, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = ex.Message }],
                    IsError = true
                };
            }
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
