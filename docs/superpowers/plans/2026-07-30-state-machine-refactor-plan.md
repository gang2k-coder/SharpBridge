# State Machine Refactor & Tool State Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unify DebugSession on SessionStateMachine, complete CallToolFilter, annotate all tools with [AllowedState], and rename sessionId→processId.

**Architecture:** SessionStateMachine owns all state transitions. CallToolFilter reads [AllowedState] from tool metadata and checks against resolved session state. DebugSessionManager resolves sessions by processId or processName.

**Tech Stack:** C# 10, MCP SDK (ModelContextProtocol), DAP via SharpDbg

---

### Task 1: Fix and complete DebugSession state unification

**Files:**
- Modify: `SharpBridge/Services/DebugSession.cs`

**Context:** The user has already removed the old `DebugSessionState` enum, added `_stateMachine`, and partially updated `LaunchAsync`/`AttachAsync`. But `_lastStop`/`LastStop` are commented out (breaking), `ContinueAndWaitAsync` still uses old `State` enum, and event handlers need updating.

- [ ] **Step 1: Restore `_lastStop` and `LastStop` (uncomment them)**

They are heavily used throughout. Just uncomment lines 42-45:

```csharp
private StopEvent? _lastStop;
private StopEvent LastStop => _lastStop
    ?? throw new InvalidOperationException("No stop event. Debugger may not be stopped.");
```

- [ ] **Step 2: Remove old `State` enum and add `CurrentState` property**

Remove the commented-out `State` enum entirely (lines 40-42 of current file). Add:

```csharp
public SessionState CurrentState => _stateMachine.Current;
```

- [ ] **Step 3: Update `ForceStopAfterLaunch`**

Uncomment the fallback block at the end (when all pause attempts fail) and use `_stateMachine`:

```csharp
if (_stateMachine.Current == SessionState.Running)
{
    LogInfo("Pause did not respond — synthesizing stopped state.");
    _stateMachine.TransitionTo(SessionState.Stopped);
    _lastStop = new StopEvent("stopped", null, true, "entry", null, 0, 0)
    {
        Note = "Process is running (did not respond to pause). " +
               "Set breakpoints and use debug_continue to reach them."
    };
}
```

Also restore the early-return check at the top of the loop:
```csharp
if (_stateMachine.Current is SessionState.Exited or SessionState.Stopped) return;
```

- [ ] **Step 4: Update `ContinueAndWaitAsync` — state guard**

Replace:
```csharp
if (CurrentState != State.Stopped)
    throw new InvalidOperationException($"Cannot continue: debugger state is {CurrentState}.");
```
With:
```csharp
if (_stateMachine.Current != SessionState.Stopped && _stateMachine.Current != SessionState.Attaching)
    throw new InvalidOperationException($"Cannot continue: debugger state is {_stateMachine.Current}.");
```

- [ ] **Step 5: Update `ContinueAndWaitAsync` — Attaching handler**

Replace the `needsContinue` logic and the "simple path" block. When Attaching, send ConfigurationDone instead of Continue:

```csharp
bool isAttaching = _stateMachine.Current == SessionState.Attaching;

using var totalCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, totalCts.Token);

// Simple path: no capture breakpoints → skip auto-continue loop
if (_bpConfigs.Count == 0)
{
    var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

    if (isAttaching)
    {
        _host!.SendRequestSync(new ConfigurationDoneRequest());
        _stateMachine.TransitionTo(SessionState.Running);
    }
    else
    {
        _stateMachine.TransitionTo(SessionState.Running);
        _host!.SendRequestSync(new ContinueRequest { ThreadId = LastStop.ThreadId ?? 0 });
    }

    try
    {
        var stopEvent = await stopTcs.Task.WaitAsync(linked.Token);
        if (_stateMachine.Current == SessionState.Exited) return LastStop;
        return BuildStopEvent(stopEvent);
    }
    catch (OperationCanceledException)
    {
        if (_stateMachine.Current == SessionState.Exited)
            return LastStop;
        LogInfo("Continue timed out — pausing.");
        var result = await PauseAndReturn(ct);
        return result with { Note = (result.Note is not null ? result.Note + " " : "") + "(timed out waiting for breakpoint)" };
    }
}
```

- [ ] **Step 6: Update `ContinueAndWaitAsync` — Go-action loop**

Replace all `CurrentState == State.Stopped` with `_stateMachine.Current == SessionState.Stopped` and `CurrentState = State.Running` with `_stateMachine.TransitionTo(SessionState.Running)` in the loop:

```csharp
StoppedEvent? loopStopEvent = null;
do
{
    var stopTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    Interlocked.Exchange(ref _pendingStopTcs, stopTcs);

    _stateMachine.TransitionTo(SessionState.Running);
    _host!.SendRequestSync(new ContinueRequest { ThreadId = LastStop.ThreadId ?? 0 });

    try
    {
        loopStopEvent = await stopTcs.Task.WaitAsync(linked.Token);
    }
    catch (OperationCanceledException)
    {
        if (_stateMachine.Current == SessionState.Exited)
            return LastStop;
        LogInfo("Continue timed out — pausing.");
        var result = await PauseAndReturn(ct);
        return result with { Note = (result.Note is not null ? result.Note + " " : "") + "(timed out waiting for breakpoint)" };
    }

    if (_stateMachine.Current == SessionState.Stopped && _shouldAutoContinue)
    {
        var (isGo, shouldCapture, scope, depth) = ResolveBreakpointAction();
        if (shouldCapture)
            CaptureState(scope, depth);
        _shouldAutoContinue = isGo;
    }
} while (_shouldAutoContinue && _stateMachine.Current == SessionState.Stopped);

if (_stateMachine.Current == SessionState.Exited)
    return LastStop;
return BuildStopEvent(loopStopEvent);
```

- [ ] **Step 7: Update `StepAsync`**

```csharp
if (_stateMachine.Current != SessionState.Stopped)
    throw new InvalidOperationException($"Cannot step: debugger state is {_stateMachine.Current}.");

_stateMachine.TransitionTo(SessionState.Running);
```

- [ ] **Step 8: Update `PauseAsync`**

```csharp
if (_stateMachine.Current != SessionState.Running)
    throw new InvalidOperationException($"Cannot pause: debugger state is {_stateMachine.Current}.");
```

- [ ] **Step 9: Update `PauseAndReturn`**

```csharp
if (_stateMachine.Current == SessionState.Running)
{
    _stateMachine.TransitionTo(SessionState.Stopped);
    _lastStop = new StopEvent("stopped", null, true, "pause", null, 0, 0)
    {
        Note = "Unable to pause. Process may be blocked in native code."
    };
}
```

- [ ] **Step 10: Update event handlers (`OnStopped`, `OnExited`, `OnTerminated`)**

Replace direct `CurrentState = State.X` with `_stateMachine.TransitionTo(SessionState.X)`:

`OnStopped`:
```csharp
private void OnStopped(StoppedEvent e)
{
    _stateMachine.TransitionTo(SessionState.Stopped);
    _lastStop = BuildStopEvent(e);
    _activeThreadId = e.ThreadId;
    // ... rest unchanged
}
```

`OnExited`:
```csharp
private void OnExited(ExitedEvent e)
{
    _stateMachine.TransitionTo(SessionState.Exited);
    _shouldAutoContinue = false;
    // ... rest unchanged
}
```

`OnTerminated`:
```csharp
private void OnTerminated(TerminatedEvent e)
{
    _stateMachine.TransitionTo(SessionState.Exited);
    _shouldAutoContinue = false;
    // ... rest unchanged
}
```

- [ ] **Step 11: Update `Disconnect`**

```csharp
if (_stateMachine.Current == SessionState.Detached) return;
```

- [ ] **Step 12: Update `Cleanup`**

```csharp
_stateMachine.TransitionTo(SessionState.Detached); // or just don't change state — cleanup is terminal
```

Actually for Cleanup, `SessionState.Detached` is the initial state. But after exit, state is `Exited`. Transitioning `Exited → Detached` is not defined in the state machine. Let's just not change state in Cleanup:

```csharp
private void Cleanup()
{
    if (_cleanedUp) return;
    _cleanedUp = true;

    _host?.Stop();
    _host?.WaitForReader();
    _adapter?.Dispose();
    _host = null;

    if (ProcessId.HasValue)
        _onDisposed?.Invoke(ProcessId.Value);
}
```

(Remove `CurrentState = State.NotStarted;`)

- [ ] **Step 13: Update `EnsureStopped`**

```csharp
private void EnsureStopped()
{
    if (_stateMachine.Current != SessionState.Stopped)
        throw new InvalidOperationException(
            $"Debugger is not stopped (state: {_stateMachine.Current}). Use debug_state first.");
}
```

- [ ] **Step 14: Remove `DebuggerState` enum at bottom of file**

Delete the unused `public enum DebuggerState { NotStarted, Running, Stopped, Exited }` at line 960.

- [ ] **Step 15: Compile check**

Run: `dotnet build SharpBridge/SharpBridge.csproj`
Expected: Build succeeds with no errors.

---

### Task 2: Extend DebugSessionManager

**Files:**
- Modify: `SharpBridge/Services/DebugSessionManager.cs`

- [ ] **Step 1: Rename `Resolve(int? sessionId)` to `Resolve(int? processId)`**

Change the parameter name only:
```csharp
public DebugSession Resolve(int? processId)
{
    var targetId = processId ?? CurrentSessionId;
    if (targetId is null)
        throw new InvalidOperationException(
            "No debug session. Use debug_launch, debug_attach, or debug_select first.");

    if (_sessions.TryGetValue(targetId.Value, out var session))
        return session;

    throw new InvalidOperationException(
        $"Session for PID {targetId.Value} not found. " +
        "It may have exited. Use debug_list to see active sessions.");
}
```

- [ ] **Step 2: Add `Resolve(string processName)` overload**

```csharp
/// <summary>
/// Resolve a session by process name. Searches active sessions first,
/// then queries the OS for a running process with the given name.
/// Requires exactly one match.
/// </summary>
public DebugSession Resolve(string processName)
{
    // 1. Search existing sessions by ProcessName
    var existing = _sessions.Values
        .FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
        return existing;

    // 2. Query OS for running processes by name
    System.Diagnostics.Process[] procs;
    try
    {
        procs = System.Diagnostics.Process.GetProcessesByName(processName);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Cannot query processes by name '{processName}': {ex.Message}");
    }

    if (procs.Length == 0)
        throw new InvalidOperationException(
            $"No process named '{processName}' is running. Start the program first, then use debug_attach.");

    if (procs.Length > 1)
        throw new InvalidOperationException(
            $"Multiple processes named '{processName}' found. Use debug_attach with a specific processId instead.");

    var pid = procs[0].Id;
    try { foreach (var p in procs) p.Dispose(); } catch { }

    throw new InvalidOperationException(
        $"No debug session for PID {pid} ('{processName}'). Use debug_attach processName=\"{processName}\" first.");
}
```

- [ ] **Step 3: Update `CurrentState` references**

`DebugSession` no longer has the old `State` enum. Change all `session.CurrentState.ToString()` to `session.CurrentState.ToString()` (this already works since `SessionState` is an enum with `.ToString()`).

- [ ] **Step 4: Compile check**

Run: `dotnet build SharpBridge/SharpBridge.csproj`
Expected: Build succeeds.

---

### Task 3: Complete CallToolFilter

**Files:**
- Modify: `SharpBridge/Infrastructure/Filters.cs`

- [ ] **Step 1: Implement the full filter logic**

Replace the entire filter with:

```csharp
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
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> SessionStateFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? "";
            var toolCollection = context.Server.ServerOptions.ToolCollection;

            // Tool not found
            if (toolCollection is null || !toolCollection.TryGetPrimitive(toolName, out var tool))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Unknown tool: {toolName}" }],
                    IsError = true
                };
            }

            // Check for [AllowedState] attribute
            var attr = tool.Metadata.OfType<AllowedStateAttribute>().FirstOrDefault();
            if (attr is null)
            {
                // No state restriction — pass through
                return await next(context, cancellationToken);
            }

            // Resolve session from tool arguments
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
            DebugSession? session;

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

            // Check state
            if (!attr.AllowedStates.Contains(session.CurrentState))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Tool '{toolName}' cannot be called in state '{session.CurrentState}'. " +
                               $"Allowed states: [{string.Join(", ", attr.AllowedStates)}]."
                    }],
                    IsError = true
                };
            }

            return await next(context, cancellationToken);
        };
    }

    private static DebugSession ResolveSession(DebugSessionManager manager, Dictionary<string, object?>? args)
    {
        // Try processId first
        if (args is not null && args.TryGetValue("processId", out var pidObj) && pidObj is not null)
        {
            if (pidObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Number)
                return manager.Resolve(jsonEl.GetInt32());
            if (pidObj is int pidInt)
                return manager.Resolve(pidInt);
        }

        // Try processName
        if (args is not null && args.TryGetValue("processName", out var nameObj) && nameObj is not null)
        {
            var name = nameObj is JsonElement jsonName ? jsonName.GetString() : nameObj.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return manager.Resolve(name!);
        }

        // Fall back to current session
        return manager.Resolve(processId: null);
    }
}
```

- [ ] **Step 2: Compile check**

Run: `dotnet build SharpBridge/SharpBridge.csproj`
Expected: Build succeeds.

---

### Task 4: Update SessionTools

**Files:**
- Modify: `SharpBridge/Tools/SessionTools.cs`

- [ ] **Step 1: Rename `sessionId` to `processId` in DebugDisconnect, DebugState, DebugSelect**

Change `[Description("Process ID. Uses the currently selected session if omitted.")] int? sessionId = null` to `int? processId = null` in all three methods. Add `string? processName = null` parameter.

Update internal calls to use the new parameter names:
- `_manager.DisconnectSession(sessionId, terminateDebuggee)` → `_manager.DisconnectSession(processId, terminateDebuggee)`
- `_manager.Resolve(sessionId)` → Call the appropriate Resolve based on what was provided
- etc.

Wait - for tools WITHOUT `[AllowedState]`, the filter doesn't resolve the session. The tool itself still needs to resolve. So the tool needs to handle both `processId` and `processName`.

Actually, let me simplify: since the tools all call `_manager.Resolve(sessionId)` currently, I should:

1. Rename the parameter
2. For tools without `[AllowedState]`, the tool itself does its own resolution
3. For tools with `[AllowedState]`, the filter checks state, then the tool resolves again (redundant but safe)

For SessionTools, none have `[AllowedState]`, so they need to resolve themselves. But the current tools only accept `sessionId`. They need to also handle `processName`.

Actually, to keep things simpler, I should add a helper that combines both. Or better: add a Resolve overload in the manager that takes both, and the tools pass through whatever the user provided.

Hmm, let me think about this differently. Currently the tools have:
```csharp
var session = _manager.Resolve(sessionId);
```

After the change, the tools need to handle:
```csharp
DebugSession session;
if (processId.HasValue)
    session = _manager.Resolve(processId.Value);
else if (!string.IsNullOrWhiteSpace(processName))
    session = _manager.Resolve(processName);
else
    session = _manager.Resolve(processId: null);
```

This is boilerplate in every tool. But it's clear and explicit. Let me just do it this way.

Actually, wait. Let me re-read the user's requirements. The user said:
- For launch: only program path
- For attach: processId OR processName
- For other tools: processId or processName or neither (use current)

So every tool (except launch/attach) needs the same pattern. Let me add a `ResolveFromArgs` helper in DebugSessionManager that takes the raw args. But the user explicitly said "不要吧args暴露给DebugSessionManager" (don't expose args to DebugSessionManager).

OK so the resolution code is duplicated in each tool AND in the filter. That's intentional for separation of concerns. Let me keep it that way.

Actually, I just realized: for tools WITH `[AllowedState]`, the filter already resolves the session and checks state. The tool then resolves again. This is fine - it's a guard pattern. The filter is a gatekeeper, not a session injector.

But wait, there's an issue: the filter resolves the session to check state, but it doesn't pass the resolved session to the tool. The tool has to resolve again. If the resolution fails in the tool (race condition: session was there during filter check but gone by the time tool runs), that's an edge case but acceptable.

OK, let me just proceed with the plan. For each tool:

1. Rename `sessionId` to `processId`
2. Add `processName` parameter
3. Replace `_manager.Resolve(sessionId)` with the three-way resolution

Let me write the plan steps for all 4 tool files.

- [ ] **Step 1: Update DebugDisconnect, DebugState, DebugSelect parameters and body**

For `DebugDisconnect`, change signature to:
```csharp
public string DebugDisconnect(
    [Description("Whether to terminate the debugged process (default: true)")] bool terminateDebuggee = true,
    [Description("Process ID. Uses the currently selected session if omitted.")] int? processId = null,
    [Description("Process name. Uses the currently selected session if omitted.")] string? processName = null)
```

Body: resolve session using processId/processName/current, then disconnect.

For `DebugState`, same parameter pattern. Body: resolve, then format state.

For `DebugSelect`, change `(int processId)` to `(int? processId = null, string? processName = null)`. The user can select by PID or by name.

- [ ] **Step 2: Compile check**

---

### Task 5: Update BreakpointTools, ExecutionTools, InspectionTools

**Files:**
- Modify: `SharpBridge/Tools/BreakpointTools.cs`
- Modify: `SharpBridge/Tools/ExecutionTools.cs`
- Modify: `SharpBridge/Tools/InspectionTools.cs`

For every tool method in these files:

- [ ] **Step 1: Add `[AllowedState]` attribute to each tool**
- [ ] **Step 2: Rename `sessionId` to `processId`, add `processName` parameter**
- [ ] **Step 3: Replace `_manager.Resolve(sessionId)` with three-way resolution**
- [ ] **Step 4: Compile check**

---

### Task 6: Wire up filter in Program.cs

**Files:**
- Modify: `SharpBridge/Program.cs`

- [ ] **Step 1: Ensure filter is registered**

Check if the filter is already wired up or needs to be added to the MCP server options.

- [ ] **Step 2: Compile and run tests**

Run: `dotnet build SharpBridge/SharpBridge.csproj`
Run: `dotnet test SharpBridge.Tests/SharpBridge.Tests.csproj`
