# SharpBridge

An MCP (Model Context Protocol) server that enables AI agents to debug .NET programs interactively. Supports **multiple simultaneous debug sessions** — launch or attach to several processes at once. Built on **.NET 10** and **SharpDbg** via the **Debug Adapter Protocol (DAP)**.

## How It Works

```
AI Agent (Claude/Copilot/...)  ←→  MCP  ←→  SharpBridge  ←→  DAP  ←→  SharpDbg  ←→  Your .NET App(s)
```

SharpBridge translates MCP tool calls into DAP debug commands, giving AI coding agents the ability to set breakpoints, step through code, inspect variables, and evaluate expressions at runtime.

## Features

- **Multi-session**: debug multiple processes simultaneously — each with its own DAP connection, breakpoints, and state
- **Launch** .NET programs with debugging, or **attach** to running processes (by PID or process name)
- **Smart attach**: auto-detect single vs. multiple process instances by name
- **Session management**: `debug_select` to switch default session, `debug_list` to see all active sessions
- **22 MCP tools**: session management, breakpoints (with auto-capture), exception breakpoints, execution control, state inspection, and capture snapshots
- **Smart inspect**: `variables_get` supports scope selection (locals/arguments/all), auto-expand depth, and targeted expansion by name — one call replaces multiple round-trips
- **Exception breakpoints**: `exception_breakpoints` lists available filters and configures which exceptions cause breaks
- **Auto-capture**: breakpoints with `action="capture"` auto-capture variables and continue (per `captureScope`/`captureDepth`), accumulating snapshots. `capture_state` / `get_captures` / `clear_captures` manage state snapshots
- **Single STDIO transport** — zero-config MCP integration

## Quick Start

### Install as .NET tool

```bash
dotnet tool install -g SharpBridge
```

### Or build from source

```bash
git clone https://github.com/gang2k-coder/SharpBridge.git
cd SharpBridge
dotnet build
```

### Register with an MCP client

**Claude Code** (`.mcp.json` in your project root):

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```

**VS Code / GitHub Copilot** (`.vscode/mcp.json`):

```json
{
  "servers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```

If built from source, replace `"sharpbridge"` with `"dotnet"` and add `"args": ["run", "--project", "/path/to/SharpBridge/SharpBridge"]`.
```

## Project Structure

```
SharpBridge/
├── SharpBridge.sln
├── SharpBridge/                      # MCP server
│   ├── Program.cs                    # Server bootstrap + DI
│   ├── Services/
│   │   ├── DebugSession.cs           # Per-process DAP client + state machine
│   │   └── DebugSessionManager.cs    # Multi-session manager + process lookup
│   └── Tools/
│       ├── SessionTools.cs           # debug_launch, attach, disconnect, state, select, list
│       ├── BreakpointTools.cs        # breakpoint_set, remove, list
│       ├── ExecutionTools.cs         # debug_continue, step, pause
│       └── InspectionTools.cs        # threads, stacktrace, variables (scope+depth+expand), evaluate, exception
├── SharpBridge.Tests/                # Integration test (attach)
├── SharpBridge.LaunchTest/           # Integration test (launch)
└── TestDebuggee/                     # Simple .NET app for testing
```

## Architecture

### Multi-session design

```
MCP Request (with optional sessionId)
    │
    ▼
DebugSessionManager (DI singleton)
    ├── ConcurrentDictionary<int, DebugSession>  (pid → session)
    ├── CurrentSessionId (debug_select)
    └── Resolve(sessionId) → routes to correct session
          │
          ▼
     DebugSession (one per process)
          ├── Own DebugAdapterHost + streams (SharpDbg in-memory)
          ├── ProcessId, ProcessName, IsAttached
          ├── State machine (NotStarted → Running/Stopped → Exited)
          └── Breakpoints, TCS-based event coordination
```

### DAP communication

Each `DebugSession` runs a `DebugProtocolHost` on a background thread that reads DAP messages from SharpDbg via in-memory streams (not stdin/stdout). For async operations (continue/step/pause), a `TaskCompletionSource<StoppedEvent>` is swapped via `Interlocked.Exchange` before the DAP command is sent; the `StoppedEvent` callback completes the TCS, unblocking the awaiting caller.

### Session lifecycle

- **Attach by PID**: checks for existing session (idempotent), verifies process exists in OS, creates session
- **Attach by name**: resolves single instance, reports ambiguity if multiple found
- **Launch**: creates session → launches program → extracts PID from SharpDbg logs → registers in manager
- **Disconnect/exit**: session auto-removed from manager; exited/terminated debuggees auto-cleanup
- **Error recovery**: DAP reader errors trigger cleanup callback to manager

## Known Issues

- **Launch PID extraction**: PID is extracted from SharpDbg log output via regex (`Process created suspended with PID: (\d+)`). This depends on SharpDbg's log format, which is not a stable API. If SharpDbg changes its log format, launch will fail with "Could not determine PID." Long-term fix: use DAP `ProcessEvent` if SharpDbg adds PID support, or manage process creation ourselves.

- **Exception filter support incomplete**: SharpDbg advertises `"all"` and `"user-unhandled"` exception breakpoint filters but hardcodes `breakOnAllExceptions = true` regardless of filter selection. Both filters currently behave identically (break on every exception). Per-exception-type configuration (`ExceptionOptions`) is not supported by SharpDbg. `exception_breakpoints` works correctly for stopping on exceptions, but fine-grained filtering requires future SharpDbg improvements.

- **Function breakpoints not yet implemented by SharpDbg**: `HandleSetFunctionBreakpointsRequest` returns an empty response with the comment "not yet fully implemented." Cannot break on function names until SharpDbg adds this.

- **Goto/GotoTargets not yet implemented by SharpDbg**: `HandleGotoRequest` and `HandleGotoTargetsRequest` handlers exist in SharpDbg but their functionality is unclear. Not exposed via SharpBridge.

- **Memory read/write, disassembly**: SharpDbg does not implement `ReadMemoryRequest`, `WriteMemoryRequest`, or `DisassembleRequest`. These require SharpDbg changes.

## License

MIT
