# SharpBridge

An MCP (Model Context Protocol) server that enables AI agents to debug .NET programs interactively. Built on **.NET 10** and **SharpDbg** via the **Debug Adapter Protocol (DAP)**.

## How It Works

```
AI Agent (Claude/Copilot/...)  ←→  MCP  ←→  SharpBridge  ←→  DAP  ←→  SharpDbg  ←→  Your .NET App
```

SharpBridge translates MCP tool calls into DAP debug commands, giving AI coding agents the ability to set breakpoints, step through code, inspect variables, and evaluate expressions at runtime.

## Features

- **Launch** .NET programs with debugging, or **attach** to running processes
- **16 MCP tools**: session management, breakpoints, execution control, and state inspection
- **DebuggerDisplay / DebuggerTypeProxy** support (via SharpDbg)
- **Single STDIO transport** — zero-config MCP integration
- **3-thread architecture**: DAP protocol loop → event channel → MCP request handler

## Quick Start

```bash
# Install
git clone https://github.com/your-org/SharpBridge.git
cd SharpBridge
dotnet build

# Register with Claude Code
# Add to .mcp.json:
{
  "mcpServers": {
    "sharpbridge": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/SharpBridge/SharpBridge"]
    }
  }
}
```

## Project Structure

```
SharpBridge/
├── SharpBridge.sln
├── SharpBridge/                # MCP server
│   ├── Program.cs              # Server bootstrap + DI
│   ├── Services/
│   │   └── DebugSession.cs     # DAP client + state machine + event bridge
│   └── Tools/
│       ├── SessionTools.cs     # debug_launch, attach, disconnect, state
│       ├── BreakpointTools.cs  # breakpoint_set, remove, list
│       ├── ExecutionTools.cs   # debug_continue, step, pause
│       └── InspectionTools.cs  # threads, stacktrace, variables, evaluate, exception
├── SharpBridge.Tests/          # Integration tests
└── TestDebuggee/               # Simple .NET app for testing
```

## Architecture

Three-thread design using `System.Threading.Channels` for lock-free event dispatch:

| Thread | Owner | Role |
|--------|-------|------|
| ① | SharpDbg | Reads DAP commands from stdin pipe → ManagedDebugger → writes responses/events to stdout pipe |
| ② | Our ReaderLoop | Reads stdout pipe → dispatches responses (TCS) and events (Channel) |
| ③ | MCP SDK | Processes AI tool calls → writes to stdin pipe → awaits TCS or reads Channel |

## License

MIT
