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
- **Target validation**: `debug_launch` verifies the file is a .NET assembly (PE CorHeader, with AppHost `.exe`→`.dll` fallback); `debug_attach` verifies the process has the CLR loaded — non-.NET targets are rejected with a clear error before any debugger work starts
- **Smart attach**: auto-detect single vs. multiple process instances by name
- **Session management**: `debug_select` to switch default session, `debug_list` to see all active sessions
- **25 MCP tools**: session management, breakpoints (source + function, with auto-capture), exception breakpoints, execution control, state inspection, and capture snapshots
- **Module introspection**: `modules_list` shows the modules (assemblies) loaded into the debugged process with their paths — useful for diagnosing pending breakpoints or verifying which assembly was loaded
- **Smart inspect**: `variables_get` supports scope selection (locals/arguments/all), auto-expand depth, and targeted expansion by name — one call replaces multiple round-trips
- **Exception breakpoints**: `exception_breakpoints` lists available filters and configures which exceptions cause breaks
- **Auto-capture**: breakpoints with `action="capture"` auto-capture variables and continue (per `captureScope`/`captureDepth`), accumulating snapshots. `capture_state` / `get_captures` / `clear_captures` manage state snapshots
- **No missed stops**: if the program stops (breakpoint or exception) while no tool call is waiting — e.g. after a `debug_continue` timed out — the pending stop is delivered on the next `debug_continue` / `debug_wait` **without resuming**, so an agent never loses a stop opportunity. `debug_state` and inspection tools acknowledge a seen stop; launch/attach reset the tracking per process
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

### Function breakpoints

`function_breakpoint_set` breaks when a method is **entered**, targeting the CLR
metadata name rather than a source line. Pattern format:

```
[TypeName.]MethodName[(ParameterTypes)]
```

| Pattern | Matches |
|---|---|
| `Calculator.Multiply` | type (exact or `.TypeName` suffix) + exact method short name |
| `Multiply` | any type with that method name |
| `Greeter.GetGreeting(string)` | single-parameter overload only |
| `MyApp.GenericProcessor<T>.Process(T)` | generic type/method by arity |

Rules:

- **Method name is matched exactly** (case-sensitive, no wildcards); the type
  segment supports exact or `.TypeName`-suffix matching.
- **Parameter types disambiguate overloads**; C# aliases (`int`, `string`, `bool`,
  `long`, …) are resolved to CLR types automatically, `?` and nested generics
  are supported. **Omitting the parameter list binds every overload** of that name.
- The module must have a **PDB**; the breakpoint binds to the method entry IL.
- Set before the module loads → reported `pending`, binds automatically when
  the module loads (`verified` flips to `true`).
- Repeated calls **accumulate** for both `breakpoint_set` (same file) and `function_breakpoint_set`: existing breakpoints are preserved and new ones are added. Note that every re-send refreshes the breakpoint IDs in that scope — use the IDs from the latest response or `breakpoint_list`.
- **Local functions and lambdas cannot be targeted**: the compiler mangles their
  names (e.g. `<<Main>$>g__SignalLoopEnd|0_0`) and `<>`/`|` cannot appear in a
  pattern. Use regular methods instead.

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

- **Attach validation waits up to 2s for young processes**: `debug_attach` gives a freshly-started process up to 2 seconds to load its CLR before the module check (a .NET process loads the CLR within ~1s of start; a non-.NET process is rejected as soon as it is 2s old). Attaching to a process you just launched may therefore take a moment.

- **stopAtEntry not implemented by SharpDbg**: `debug_launch` waits briefly for an entry-point stop; SharpDbg does not send one, so launch returns `running` (honest state). Set breakpoints and use `debug_continue`; the stop ledger delivers any stop that occurs while no tool call is waiting.

- **PDB symbols — where SharpDbg looks and what it checks**: symbols are loaded from the `.pdb` file **next to the module's `.dll`** (same directory, same base name); SharpDbg does not search other locations (no symbol server, no CodeView original path). Mismatched/stale PDBs are rejected by a GUID+age check and reported as "No symbols found". Embedded PDBs (`<DebugType>embedded</DebugType>`) are supported and recommended (self-contained, can never go stale). Without symbols, source breakpoints cannot bind: `breakpoint_set` reports `failed`, and its hint now attributes the cause — "no loaded module has PDB symbols" (build Debug / use embedded) vs "modules with symbols exist but this file/line did not resolve" (check `filePath` against the path recorded in the PDB). Note: SharpDbg also falls back to generating a decompiled `.pdb` for symbol-less modules, but those have virtual document paths that will not match your source paths.

- **Launch PID extraction**: PID is extracted from SharpDbg log output via regex (`Process created suspended with PID: (\d+)`). This depends on SharpDbg's log format, which is not a stable API. If SharpDbg changes its log format, launch will fail with "Could not determine PID." Long-term fix: use DAP `ProcessEvent` if SharpDbg adds PID support, or manage process creation ourselves.

- **Exception filter support incomplete**: SharpDbg advertises `"all"` and `"user-unhandled"` exception breakpoint filters but hardcodes `breakOnAllExceptions = true` regardless of filter selection. Both filters currently behave identically (break on every exception). Per-exception-type configuration (`ExceptionOptions`) is not supported by SharpDbg. `exception_breakpoints` works correctly for stopping on exceptions, but fine-grained filtering requires future SharpDbg improvements.

- **Function breakpoints cannot target local functions or lambdas**: C# compiles these to compiler-generated names — e.g. `static void SignalLoopEnd()` in top-level statements becomes `<<Main>$>g__SignalLoopEnd|0_0` (the `<>`/`|` characters are reserved and cannot appear in a typed identifier). SharpDbg matches the method name **exactly** against the CLR metadata name (short name only — no namespace/type prefix), while type names support exact or `.TypeName` suffix matching, so a `function_breakpoint_set` for a local function never binds. Use regular methods instead (e.g. `LoopEnd.Signal`). Note that a function breakpoint set before the target module is loaded is reported as unverified and binds automatically when the module loads — `verified=false` at set time is normal in that case.

- **breakpoint_set accumulates within a file**: multiple breakpoints in the same file are preserved across calls (each call adds one). Re-sends refresh the file's breakpoint IDs — use the IDs from the latest response or `breakpoint_list`.

- **Module list reflects LoadModule events only**: `modules_list` is populated from SharpDbg's LoadModule callbacks — modules are only added (no unload tracking), and the list is empty while the CLR is frozen (during `Attaching`, i.e. before the first `debug_continue`). Only id/name/path are available; PDB/symbol status is not exposed by SharpDbg yet.

- **Goto/GotoTargets not yet implemented by SharpDbg**: `HandleGotoRequest` and `HandleGotoTargetsRequest` handlers exist in SharpDbg but their functionality is unclear. Not exposed via SharpBridge.

- **Memory read/write, disassembly**: SharpDbg does not implement `ReadMemoryRequest`, `WriteMemoryRequest`, or `DisassembleRequest`. These require SharpDbg changes.

## License

MIT
