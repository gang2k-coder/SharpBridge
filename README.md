# SharpBridge

An MCP (Model Context Protocol) server that enables AI agents to debug .NET programs interactively. Supports **multiple simultaneous debug sessions** — launch or attach to several processes at once. Built on **.NET 10** and **SharpDbg** via the **Debug Adapter Protocol (DAP)**.

## How It Works

```
AI Agent (Claude/Copilot/...)  ←→  MCP  ←→  SharpBridge  ←→  DAP  ←→  SharpDbg  ←→  Your .NET App(s)
```

SharpBridge translates MCP tool calls into DAP debug commands, giving AI coding agents the ability to set breakpoints, step through code, inspect variables, and evaluate expressions at runtime. Under the hood it uses [SharpDbg](https://github.com/MattParkerDev/SharpDbg), a .NET debug adapter built on ICorDebug — the same COM-based debugging API that Visual Studio uses.

## Features

- **Multi-session**: debug multiple processes simultaneously — each with its own DAP connection, breakpoints, and state
- **Launch** .NET programs with debugging, or **attach** to running processes (by PID or process name)
- **Smart attach**: auto-detect single vs. multiple process instances by name
- **Session management**: `debug_select` to switch default session, `debug_list` to see all active sessions
- **24 MCP tools**: session management, breakpoints (source + function, with auto-capture), exception breakpoints, execution control, state inspection, and capture snapshots
- **Smart inspect**: `variables_get` supports scope selection (locals/arguments/all), auto-expand depth, and targeted expansion by name — one call replaces multiple round-trips
- **Exception breakpoints**: `exception_breakpoints` lists available filters and configures which exceptions cause breaks
- **Auto-capture**: breakpoints with `action="capture"` auto-capture variables and continue (per `captureScope`/`captureDepth`), accumulating snapshots. `capture_state` / `get_captures` / `clear_captures` manage state snapshots
- **Single STDIO transport** — zero-config MCP integration

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building/running .NET apps to debug)
- A supported AI coding agent (Claude Desktop, Claude Code, Cursor, GitHub Copilot, etc.)

### Option 1: Install from NuGet (recommended)

```bash
dotnet tool install -g SharpBridge
```

Then add this to your MCP config (see [client-specific paths below](#configure-your-mcp-client)):

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```

### Option 2: Build from source

```bash
git clone https://github.com/gang2k-coder/SharpBridge.git
cd SharpBridge
dotnet build
```

When built from source, use this MCP config instead:

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/SharpBridge/SharpBridge"]
    }
  }
}
```

### Configure your MCP client

Add SharpBridge to your MCP client's configuration file:

<details>
<summary><b>Claude Code</b></summary>

Create or edit `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```
</details>

<details>
<summary><b>Claude Desktop</b></summary>

Edit `claude_desktop_config.json`:
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```
</details>

<details>
<summary><b>Cursor</b></summary>

Edit `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```

Restart Cursor after saving.
</details>

<details>
<summary><b>VS Code / GitHub Copilot</b></summary>

Create `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "sharpbridge": {
      "command": "sharpbridge"
    }
  }
}
```
</details>

### Verify installation

Start a debuggee (any .NET app), then ask your AI agent:

> Attach to process XYZ and set a breakpoint at Program.cs:10

If the agent can list tools (`debug_attach`, `breakpoint_set`, etc.) via SharpBridge, you're set up.

### Basic workflow

Typical debugging session with your AI agent:

```
1. debug_launch (or debug_attach) — start debugging
2. breakpoint_set — set breakpoints on interesting lines
3. debug_continue  — run until a breakpoint hits
4. variables_get   — inspect program state
5. debug_step      — step through code line by line
6. debug_disconnect — clean up when done
```

## Project Structure

```
SharpBridge/
├── SharpBridge.sln
├── SharpBridge/                      # MCP server
│   ├── Program.cs                    # Server bootstrap + DI
│   ├── Infrastructure/
│   │   ├── Attributes.cs             # [AllowedState] attribute
│   │   └── Filters.cs                # CallToolFilter — per-tool state enforcement
│   ├── State/
│   │   └── SessionState.cs           # SessionStateMachine (Detached→Attaching→Running↔Stopped→Exited)
│   ├── Services/
│   │   ├── DebugSession.cs           # Per-process DAP client + state machine
│   │   └── DebugSessionManager.cs    # Multi-session manager + process lookup
│   └── Tools/
│       ├── SessionTools.cs           # debug_launch, attach, disconnect, state, select, list
│       ├── BreakpointTools.cs        # breakpoint_set, function_breakpoint_set, remove, list
│       ├── ExecutionTools.cs         # debug_continue, step, pause
│       └── InspectionTools.cs        # threads, stacktrace, variables (scope+depth+expand), evaluate, exception
├── SharpBridge.Tests/                # Integration test (attach)
├── SharpBridge.LaunchTest/           # Integration test (launch)
└── TestDebuggee/                     # Simple .NET app for testing
```

## Architecture

### Multi-session design

```
MCP Request (with optional processId/processName)
    │
    ▼
DebugSessionManager (DI singleton)
    ├── ConcurrentDictionary<int, DebugSession>  (pid → session)
    ├── CurrentSessionId (debug_select)
    ├── Resolve(int? processId) / Resolve(string processName)
    └── CallToolFilter — enforces [AllowedState] per-tool constraints
          │
          ▼
     DebugSession (one per process)
          ├── Own DebugAdapterHost + streams (SharpDbg in-memory)
          ├── ProcessId, ProcessName, IsAttached
          ├── State machine (Detached → Attaching → Running ↔ Stopped → Exited)
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

- **Goto/GotoTargets not yet implemented by SharpDbg**: `HandleGotoRequest` and `HandleGotoTargetsRequest` handlers exist in SharpDbg but their functionality is unclear. Not exposed via SharpBridge.

- **Memory read/write, disassembly**: SharpDbg does not implement `ReadMemoryRequest`, `WriteMemoryRequest`, or `DisassembleRequest`. These require SharpDbg changes.

## License

MIT
