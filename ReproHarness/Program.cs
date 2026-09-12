using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpBridge.Services;
using SharpBridge.State;

// ===================================================================
// Repro harness — mimics the REAL MCP tool layer (state filter) on top of
// the raw service calls the integration tests use.
//
// attach mode: two capture breakpoints (lines 11+12 of ReproDebuggee).
// The debugger ping-pongs the debuggee between the two lines on every
// auto-continue — the tightest possible loop for the state-machine race.
// ===================================================================

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var testDebuggeeDll = Path.Combine(repoRoot, "TestDebuggee/bin/Debug/net10.0/TestDebuggee.dll");
var reproDebuggeeDll = Path.Combine(repoRoot, "ReproDebuggee/bin/Debug/net10.0/ReproDebuggee.dll");
var reproDebuggeeSrc = Path.Combine(repoRoot, "ReproDebuggee/Program.cs");

// HARNESS_LOG=debug surfaces the full DAP/SharpDbg message trace (very
// verbose) — needed to diagnose a wedged run at the event level.
var harnessLogLevel = string.Equals(
    Environment.GetEnvironmentVariable("HARNESS_LOG"), "debug", StringComparison.OrdinalIgnoreCase)
    ? LogLevel.Debug
    : LogLevel.Warning;
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(harnessLogLevel));

// AllowedState for debug_continue (from ExecutionTools)
SessionState[] continueAllowed = [SessionState.Attaching, SessionState.Stopped, SessionState.Running];
// AllowedState for breakpoint_set (from BreakpointTools)
SessionState[] bpAllowed = [SessionState.Attaching, SessionState.Stopped, SessionState.Running];

var mode = args.Length > 0 ? args[0] : "attach";
Console.WriteLine($"\n=== MODE: {mode} ===");

// HARNESS_ROUNDS / HARNESS_TIMEOUT shrink a run so many samples fit in a
// comparison sweep (defaults match the historical 6 rounds x 8s).
var harnessRounds = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_ROUNDS"), out var hr) ? hr : 6;
var harnessTimeout = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_TIMEOUT"), out var ht) ? ht : 8;

// Independent ground truth for "is the debuggee actually running?".
// ReproDebuggee spins a tight loop, so a 200ms CPU-time delta separates a
// running debuggee (~full CPU) from one stopped by the debugger (~0).
static string DebuggeeActivity(Process proc)
{
    try
    {
        var t1 = proc.TotalProcessorTime;
        Thread.Sleep(200);
        proc.Refresh();
        var t2 = proc.TotalProcessorTime;
        var states = proc.Threads.Cast<ProcessThread>()
            .Select(t => $"{t.ThreadState}/{t.WaitReason}")
            .Distinct();
        return $"cpuDelta={(t2 - t1).TotalMilliseconds:F1}ms threads=[{string.Join(", ", states)}]";
    }
    catch (Exception ex) { return "n/a (" + ex.Message + ")"; }
}

async Task<StopEvent?> AgentContinue(DebugSession session, string label, int timeout = 10)
{
    // Mimic SessionStateFilter: state pre-check (the session gate is gone
    // since S2 — the session consumer serializes the real work).
    if (!continueAllowed.Contains(session.CurrentState))
    {
        Console.WriteLine($"[agent:{label}] debug_continue REJECTED by state filter: " +
                          $"state={session.CurrentState}, allowed=[{string.Join(",", continueAllowed)}]");
        return null;
    }

    Console.WriteLine($"[agent:{label}] debug_continue -> state={session.CurrentState}, timeout={timeout}s ...");
    var stop = await session.ContinueAndWaitAsync(timeout).ConfigureAwait(false);
    Console.WriteLine($"[agent:{label}] debug_continue returned: status={stop.Status}, reason={stop.Reason}, " +
                      $"line={stop.Line}, state={session.CurrentState}");
    if (stop.Note is not null)
        Console.WriteLine($"                  note: {stop.Note.Substring(0, Math.Min(80, stop.Note.Length))}");
    return stop;
}

if (mode == "launch")
{
    // ============ SCENARIO A: LAUNCH (real-agent flow, TestDebuggee) ============
    using var session = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    try
    {
        Console.WriteLine("1. debug_launch (stopAtEntry=true)...");
        await session.LaunchAsync(testDebuggeeDll, null, Path.GetDirectoryName(testDebuggeeDll), true, null);
        Console.WriteLine($"   -> state after launch: {session.CurrentState}  (pid={session.ProcessId})");
        await Task.Delay(3000);
        Console.WriteLine($"   -> state after 3s: {session.CurrentState}");

        Console.WriteLine("2. breakpoint_set action=capture @ line 51 (absolute path)...");
        var bps = session.SetBreakpoints(
            Path.Combine(repoRoot, "TestDebuggee/Program.cs"),
            (Line: 51, Column: null, Condition: null, HitCondition: null,
             Action: "capture", CaptureScope: "all", CaptureDepth: 0, CaptureExpressions: null));
        Console.WriteLine($"   -> verified={bps[0].Verified}, message={bps[0].Message}, line={bps[0].Line}");

        Console.WriteLine("3. debug_continue (agent)...");
        await AgentContinue(session, "first");

        Console.WriteLine("4. debug_state (agent)...");
        session.ObserveStopState();
        Console.WriteLine($"   -> state={session.CurrentState}");

        Console.WriteLine("5. debug_continue again (agent retry)...");
        await AgentContinue(session, "retry");

        await Task.Delay(3000);
        Console.WriteLine($"6. captures: {session.GetCaptures().Count}, final state={session.CurrentState}");
        try
        {
            var p = Process.GetProcessById(session.ProcessId!.Value);
            Console.WriteLine($"   debuggee running={!p.HasExited}");
        }
        catch { Console.WriteLine("   debuggee process GONE"); }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ LAUNCH scenario exception: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        try { session.Disconnect(true); } catch { }
    }
}
else if (mode == "inc")
{
    // ============ SCENARIO C: incremental breakpoints + two continues ============
    // Mirrors E2E test 17 (BreakpointTools accumulates a file's breakpoints and
    // re-sends them): bp 51, then bp 51+53, then continue -> stop at 51,
    // continue -> stop at 53. HARNESS_REPEATS=N loops the scenario.
    var testDebuggeeSrc = Path.Combine(repoRoot, "TestDebuggee/Program.cs");
    var repeats = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_REPEATS"), out var rep) ? rep : 1;

    static (int, int?, string?, string?, string, string?, int, string[]?)[ ] Acc(
        DebugSession s, string file,
        params (int Line, int? Column, string? Condition, string? HitCondition, string Action, string? CaptureScope, int CaptureDepth, string[]? CaptureExpressions)[] add)
    {
        var list = s.GetAllBreakpoints()
            .Where(bp => bp.FunctionName is null && bp.FilePath == file)
            .Select(bp => (bp.Line, bp.Column, bp.Condition, bp.HitCondition, bp.Action, bp.CaptureScope, bp.CaptureDepth, bp.CaptureExpressions))
            .ToList();
        list.AddRange(add);
        return list.ToArray();
    }

    for (int run = 0; run < repeats; run++)
    {
        var psiC = new ProcessStartInfo("dotnet", [testDebuggeeDll])
        {
            RedirectStandardOutput = true, RedirectStandardInput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        psiC.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        using var debuggeeC = Process.Start(psiC)!;

        // HARNESS_TOOL_LAYER=1 drives the REAL MCP tool classes (BreakpointTools /
        // ExecutionTools) instead of the session API — mirrors the E2E path
        // (accumulating breakpoint_set + debug_continue) without MCP stdio.
        if (Environment.GetEnvironmentVariable("HARNESS_TOOL_LAYER") is { Length: > 0 })
        {
            using var manager = new SharpBridge.Services.DebugSessionManager(loggerFactory.CreateLogger<DebugSession>());
            var attachRes = await manager.CreateAndAttachByPidAsync(debuggeeC.Id);
            var bps = new SharpBridge.Tools.BreakpointTools(manager);
            var exec = new SharpBridge.Tools.ExecutionTools(manager);
            await debuggeeC.StandardInput.WriteLineAsync();

            bps.BreakpointSet(testDebuggeeSrc, 51, processId: debuggeeC.Id);
            bps.BreakpointSet(testDebuggeeSrc, 53, processId: debuggeeC.Id);
            var j1 = System.Text.Json.JsonDocument.Parse(await exec.DebugContinue(10, debuggeeC.Id));
            var j2 = System.Text.Json.JsonDocument.Parse(await exec.DebugContinue(10, debuggeeC.Id));
            var s1 = j1.RootElement.GetProperty("status").GetString();
            var s2 = j2.RootElement.GetProperty("status").GetString();
            var ok = s1 == "stopped" && s2 == "stopped";
            Console.WriteLine($"run{run}: tool c1={s1} c2={s2} {(ok ? "OK" : "MISMATCH")}");
            try { manager.DisconnectSession(debuggeeC.Id, true); } catch { }
            if (!debuggeeC.HasExited) { try { debuggeeC.Kill(); } catch { } }
            continue;
        }

        using var sessionC = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
        try
        {
            await sessionC.AttachAsync(debuggeeC.Id);
            await debuggeeC.StandardInput.WriteLineAsync();

            sessionC.SetBreakpoints(testDebuggeeSrc, Acc(sessionC, testDebuggeeSrc, (51, null, null, null, "break", "all", 0, null)));
            sessionC.SetBreakpoints(testDebuggeeSrc, Acc(sessionC, testDebuggeeSrc, (53, null, null, null, "break", "all", 0, null)));

            var c1 = await sessionC.ContinueAndWaitAsync(10);
            var c2 = await sessionC.ContinueAndWaitAsync(10);
            var ok = c1.Status == "stopped" && c1.Line == 51 && c2.Status == "stopped" && c2.Line == 53;
            Console.WriteLine($"run{run}: c1={c1.Status}@{c1.Line} c2={c2.Status}@{c2.Line} {(ok ? "OK" : "MISMATCH")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"run{run}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { sessionC.Disconnect(true); } catch { }
            if (!debuggeeC.HasExited) { try { debuggeeC.Kill(); } catch { } }
        }
    }
}
else
{
    // ============ SCENARIO B: ATTACH + capture race (ReproDebuggee) ============
    Console.WriteLine($"Debuggee: {reproDebuggeeDll}");
    Console.WriteLine($"Source:   {reproDebuggeeSrc}");

    var psi = new ProcessStartInfo("dotnet", reproDebuggeeDll)
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee = Process.Start(psi)!;
    int pid = debuggee.Id;
    Console.WriteLine($"debuggee pid={pid} (suspended)");

    using var session = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    try
    {
        Console.WriteLine("1. debug_attach...");
        await session.AttachAsync(pid);
        Console.WriteLine($"   -> state: {session.CurrentState}");

        Console.WriteLine("2. breakpoint_set: TWO capture bps @ lines 11 and 12...");
        session.SetBreakpoints(reproDebuggeeSrc,
            (Line: 11, Column: null, Condition: null, HitCondition: null,
             Action: "capture", CaptureScope: "all", CaptureDepth: 0, CaptureExpressions: null),
            (Line: 12, Column: null, Condition: null, HitCondition: null,
             Action: "capture", CaptureScope: "all", CaptureDepth: 0, CaptureExpressions: null));
        Console.WriteLine("   -> set (pending until module loads)");

        Console.WriteLine("3. debuggee ENTER, then agent continues in rounds...");
        await debuggee.StandardInput.WriteLineAsync();

        bool frozen = false;
        for (int round = 0; round < harnessRounds && !frozen; round++)
        {
            var stop = await AgentContinue(session, $"r{round}", harnessTimeout);
            if (stop is { Status: "exited" })
            {
                Console.WriteLine("   debuggee exited — no freeze observed this run.");
                break;
            }

            // Freeze probe: when the state machine says Running, ask SharpDbg
            // for the truth. If the debuggee is actually stopped, pause fails
            // with "The process is not running..." — that is the corruption.
            if (session.CurrentState == SessionState.Running)
            {
                // HARNESS_PAUSE_DELAY_MS lets a run delay the probe: used to test
                // whether a Pause issued immediately after an auto-continue
                // resume lands in the adapter's resume window.
                if (int.TryParse(Environment.GetEnvironmentVariable("HARNESS_PAUSE_DELAY_MS"), out var pauseDelay)
                    && pauseDelay > 0)
                {
                    await Task.Delay(pauseDelay);
                }
                try
                {
                    var p = await session.PauseAsync();
                    Console.WriteLine($"   pause probe OK: status={p.Status} (it really was running)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   >>> pause probe FAILED: {ex.Message}");
                    Console.WriteLine($"   >>> debuggee activity: {DebuggeeActivity(debuggee)}");
                    if (ex.Message.Contains("not running"))
                    {
                        // Distinguish a permanent state-machine wedge from the
                        // inherent in-flight window (the debuggee stopped at the
                        // adapter, but its StoppedEvent has not been processed
                        // yet — no client-side state machine can know earlier).
                        // A real corruption keeps claiming Running with nothing
                        // pending; a transient race is resolved by the arriving
                        // stop (or its capture auto-continue) within ms.
                        var recovered = false;
                        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
                        while (DateTime.UtcNow < deadline)
                        {
                            if (session.CurrentState != SessionState.Running || session.HasUnobservedStop)
                            {
                                recovered = true;
                                break;
                            }
                            await Task.Delay(25);
                        }

                        if (recovered)
                        {
                            Console.WriteLine("   >>> (transient) pause raced a stop that arrived right after");
                            Console.WriteLine($"   >>> state now: {session.CurrentState}, pendingStop={session.HasUnobservedStop}");
                        }
                        else
                        {
                            Console.WriteLine("   >>> CORRUPTION CONFIRMED: state=Running but debuggee is actually STOPPED.");
                            Console.WriteLine("   >>> What the agent sees next (the dead end):");
                            await AgentContinue(session, "dead-end");
                            frozen = true;
                        }
                    }
                }
            }
        }

        Console.WriteLine($"7. final: state={session.CurrentState}, captures={session.GetCaptures().Count}, " +
                          $"debuggee exited={debuggee.HasExited}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ ATTACH scenario exception: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        try { session.Disconnect(true); } catch { }
        if (!debuggee.HasExited) { try { debuggee.Kill(); } catch { } }
    }
}

Console.WriteLine("\n=== HARNESS DONE ===");
