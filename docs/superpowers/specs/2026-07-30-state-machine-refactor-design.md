# State Machine Refactor & Tool State Guard

**Date:** 2026-07-30
**Status:** Approved

## Overview

Refactor SharpBridge to use a formal state machine (`SessionStateMachine`) for debug session lifecycle, a declarative `[AllowedState]` attribute to constrain which MCP tools can be invoked in which states, and a `CallToolFilter` to enforce those constraints at the MCP protocol layer.

## 1. Session State Machine

### States

| State | Meaning |
|-------|---------|
| `Detached` | No launch/attach request sent yet |
| `Attaching` | Launch/attach request sent, ConfigurationDone not yet sent |
| `Running` | ConfigurationDone sent, process executing |
| `Stopped` | StoppedEvent received, debugger paused |
| `Exited` | Process exited (terminal) |

### Transitions

```
Detached → Attaching     (launch/attach request sent)
Attaching → Running       (ConfigurationDone sent)
Running → Stopped         (StoppedEvent received)
Stopped → Running         (Continue/Step sent)
Any → Exited              (process exit)
```

Invalid transitions throw `InvalidOperationException`.

File: `SharpBridge/State/SessionState.cs` (already implemented)

## 2. AllowedState Attribute

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowedStateAttribute : Attribute
{
    public IReadOnlyCollection<SessionState> AllowedStates { get; }
    public AllowedStateAttribute(params SessionState[] states) { ... }
}
```

File: `SharpBridge/Infrastructure/Attributes.cs` (already implemented)

## 3. CallToolFilter

### Logic

```
1. Look up tool from ToolCollection by Name
2. Get [AllowedState] from tool metadata
3. If no attribute → pass through (no state check)
4. If attribute present:
   a. Parse processId/processName from call arguments
   b. Resolve target session via DebugSessionManager
   c. If session not found → return error
   d. Check session.CurrentState in AllowedStates
   e. If disallowed → return error with current state info
   f. If allowed → pass through
```

### Session Resolution (in Filter)

- `processId` provided → `DebugSessionManager.Resolve(processId)`
- `processName` provided → `DebugSessionManager.Resolve(processName)`
- Neither → `DebugSessionManager.Resolve(processId: null)` (uses CurrentSessionId)
- No session found → return error

File: `SharpBridge/Infrastructure/Filters.cs` (requires completion)

## 4. DebugSessionManager Extensions

### New Overload

```csharp
public DebugSession Resolve(string processName)
```

Logic:
1. Search `_sessions` for matching `ProcessName` → return if found
2. Query OS for processes by name:
   - 0 matches → throw "No process named 'X' running"
   - 1 match → check `_sessions` for PID; if not found, throw "No debug session. Use debug_attach first."
   - Multiple matches → throw "Multiple processes named 'X', use processId"

### Existing Resolve Updated

```csharp
public DebugSession Resolve(int? processId)
```
`null` → falls back to `CurrentSessionId`.

File: `SharpBridge/Services/DebugSessionManager.cs`

## 5. DebugSession State Unification

- Remove old `State` enum and `DebuggerState` enum
- All state: `_stateMachine.Current` (type `SessionState`)
- All transitions: `_stateMachine.TransitionTo(...)`
- Public property: `public SessionState CurrentState => _stateMachine.Current;`

State mapping for migration:
- `State.NotStarted` → `SessionState.Detached`
- `State.Running` → `SessionState.Running`
- `State.Stopped` → `SessionState.Stopped`
- `State.Exited` → `SessionState.Exited`

File: `SharpBridge/Services/DebugSession.cs`

## 6. Tool Annotations

### No annotation (always allowed)
`DebugLaunch`, `DebugAttach`, `DebugDisconnect`, `DebugState`, `DebugSelect`, `DebugList`

### `[AllowedState(SessionState.Stopped)]`
`ThreadsList`, `StacktraceGet`, `VariablesGet`, `VariablesExpand`, `Evaluate`, `ExceptionInfo`, `CaptureState`, `DebugStep`

### `[AllowedState(SessionState.Attaching, SessionState.Stopped)]`
`DebugContinue` (sends ConfigurationDone in Attaching, ContinueRequest in Stopped)

### `[AllowedState(SessionState.Running)]`
`DebugPause`

### `[AllowedState(SessionState.Stopped, SessionState.Running)]`
`BreakpointSet`, `BreakpointRemove`, `FunctionBreakpointSet`, `ExceptionBreakpoints`, `BreakpointList`, `GetCaptures`, `ClearCaptures`

## 7. Tool Parameter Changes

All tools: `sessionId` → `processId` + add optional `processName`.

- Launch: `program` only (unchanged)
- Attach: `processId?` OR `processName?` (mutually exclusive, unchanged)
- All others: `processId?` + `processName?` (neither → uses CurrentSessionId)

## 8. Files Changed

| File | Change |
|------|--------|
| `State/SessionState.cs` | No change (complete) |
| `Infrastructure/Attributes.cs` | No change (complete) |
| `Infrastructure/Filters.cs` | Complete the filter logic |
| `Services/DebugSession.cs` | Remove old enums, use _stateMachine throughout |
| `Services/DebugSessionManager.cs` | Add Resolve(string processName), rename sessionId→processId |
| `Tools/SessionTools.cs` | Params, annotations, Resolve calls |
| `Tools/BreakpointTools.cs` | Params, annotations, Resolve calls |
| `Tools/ExecutionTools.cs` | Params, annotations, Resolve calls |
| `Tools/InspectionTools.cs` | Params, annotations, Resolve calls |
