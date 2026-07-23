using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpBridge.Services;
using SharpBridge.Tools;

// ===================================================================
// SharpBridge MCP Server — AI-driven .NET debugging
// ===================================================================

var builder = Host.CreateApplicationBuilder(args);

// Suppress MCP SDK logs from stdout (stdout is the JSON-RPC channel!)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning);

// Register DebugSessionManager as singleton — manages all debug sessions
builder.Services.AddSingleton<DebugSessionManager>();

// Build MCP server with tools and STDIO transport
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "SharpBridge",
        Version = "0.1.0"
    };
    options.ServerInstructions = """
        SharpBridge is a .NET debugger MCP server. It can launch, attach to,
        and debug .NET programs. Use the tools to set breakpoints, step through
        code, inspect variables, and evaluate expressions at runtime.

        Typical workflow:
        1. debug_launch: start a .NET program with debugging
        2. breakpoint_set: set breakpoints on interesting lines
        3. debug_continue: run until a breakpoint hits
        4. stacktrace_get + variables_get: inspect program state
        5. debug_step: step through code line by line
        6. debug_disconnect: clean up when done
        """;
})
.WithTools<SessionTools>()
.WithTools<BreakpointTools>()
.WithTools<ExecutionTools>()
.WithTools<InspectionTools>()
.WithStdioServerTransport();

var host = builder.Build();
await host.RunAsync();
