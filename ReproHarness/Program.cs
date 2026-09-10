using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpBridge.Services;
using SharpBridge.State;

// ===================================================================
// Repro harness — mimics the REAL MCP tool layer (state filter + session
// gate) on top of the raw service calls the integration tests use.
//
// attach mode: two capture breakpoints (lines 11+12 of ReproDebuggee).
// The debugger ping-pongs the debuggee between the two lines on every
// auto-continue — the tightest possible loop for the state-machine race.
// ===================================================================

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var testDebuggeeDll = Path.Combine(repoRoot, "TestDebuggee/bin/Debug/net10.0/TestDebuggee.dll");
var reproDebuggeeDll = Path.Combine(repoRoot, "ReproDebuggee/bin/Debug/net10.0/ReproDebuggee.dll");
var reproDebuggeeSrc = Path.Combine(repoRoot, "ReproDebuggee/Program.cs");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

// AllowedState for debug_continue (from ExecutionTools)
SessionState[] continueAllowed = [SessionState.Attaching, SessionState.Stopped, SessionState.Running];
// AllowedState for breakpoint_set (from BreakpointTools)
SessionState[] bpAllowed = [SessionState.Attaching, SessionState.Stopped, SessionState.Running];

var mode = args.Length > 0 ? args[0] : "attach";
Console.WriteLine($"\n=== MODE: {mode} ===");

async Task<StopEvent?> AgentContinue(DebugSession session, string label, int timeout = 10)
{
    // Mimic SessionStateFilter: state check + session gate
    if (!continueAllowed.Contains(session.CurrentState))
    {
        Console.WriteLine($"[agent:{label}] debug_continue REJECTED by state filter: " +
                          $"state={session.CurrentState}, allowed=[{string.Join(",", continueAllowed)}]");
        return null;
    }

    Console.WriteLine($"[agent:{label}] debug_continue -> state={session.CurrentState}, timeout={timeout}s ...");
    var stop = await session.WithSessionLockAsync(async () =>
        await session.ContinueAndWaitAsync(timeout).ConfigureAwait(false));
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
        for (int round = 0; round < 6 && !frozen; round++)
        {
            var stop = await AgentContinue(session, $"r{round}", 8);
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
                try
                {
                    var p = await session.PauseAsync();
                    Console.WriteLine($"   pause probe OK: status={p.Status} (it really was running)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   >>> pause probe FAILED: {ex.Message}");
                    if (ex.Message.Contains("not running"))
                    {
                        Console.WriteLine("   >>> CORRUPTION CONFIRMED: state=Running but debuggee is actually STOPPED.");
                        Console.WriteLine("   >>> What the agent sees next (the dead end):");
                        await AgentContinue(session, "dead-end");
                        frozen = true;
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
