using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpBridge.Services;
using SharpBridge.State;

// ===================================================================
// Launch-mode integration test
// Launches the debuggee via SharpDbg's internal console path
// Tests GitHub Issue #1: Launch/DbgShim hang on Windows
// ===================================================================

Console.WriteLine("=== SharpBridge Launch Test ===");
Console.WriteLine();

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var debuggeeDll = Path.Combine(repoRoot, "TestDebuggee/bin/Debug/net10.0/TestDebuggee.dll");
var sourceFile = Path.Combine(repoRoot, "TestDebuggee/Program.cs");
Console.WriteLine($"RepoRoot: {repoRoot}");

Console.WriteLine($"Debuggee: {debuggeeDll}");
Console.WriteLine($"Source:   {sourceFile}");
Console.WriteLine();

// Verify the DLL exists
if (!File.Exists(debuggeeDll))
{
    Console.WriteLine($"❌ Debuggee DLL not found: {debuggeeDll}");
    Console.WriteLine("   Build TestDebuggee first.");
    return;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
using var session = new DebugSession(loggerFactory.CreateLogger<DebugSession>());

try
{
    // === Step 1: Launch ===
    Console.WriteLine("1. Launching debuggee via DAP (stopAtEntry=true)...");

    using var launchCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await session.LaunchAsync(
            program: debuggeeDll,
            cwd: Path.GetDirectoryName(debuggeeDll),
            stopAtEntry: true,
            ct: launchCts.Token);
        Console.WriteLine($"   ✅ LaunchAsync returned. State={session.CurrentState}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"   ⚠️ LaunchAsync timed out after 30s! State={session.CurrentState}");
        Console.WriteLine("   Expected: SharpDbg 0.1.17+ delivers an entry stop when");
        Console.WriteLine("   stopAtEntry=true — a timeout here is a regression.");
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"   ⚠️ DAP timeout: {ex.Message}");
        Console.WriteLine($"   State={session.CurrentState}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ❌ LaunchAsync failed: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
            Console.WriteLine($"   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    Console.WriteLine();
    Console.WriteLine("4. Disconnecting...");
    try { session.Disconnect(true); } catch (Exception ex) { Console.WriteLine($"   Disconnect failed: {ex.Message}"); }
    Console.WriteLine($"   Final State: {session.CurrentState}");

    Console.WriteLine();
    Console.WriteLine("=== Step 2: launch with stopAtEntry=true → entry stop → breakpoint ===");
    Console.WriteLine("   SharpDbg 0.1.17+ implements stopAtEntry (entry breakpoint at Main).");
    Console.WriteLine("   After launch the session must be Stopped at the entry stop.");

    void Assert(bool condition, string msg)
    {
        if (!condition) throw new Exception($"Assertion failed: {msg}");
    }

    var reproDll = Path.Combine(repoRoot, "ReproDebuggee/bin/Debug/net10.0/ReproDebuggee.dll");
    var reproSrc = Path.Combine(repoRoot, "ReproDebuggee/Program.cs");
    Console.WriteLine($"   Debuggee: {reproDll}");

    using var session2 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    try
    {
        await session2.LaunchAsync(
            program: reproDll,
            cwd: Path.GetDirectoryName(reproDll),
            stopAtEntry: true);
        Console.WriteLine($"   State after launch: {session2.CurrentState}");
        Assert(session2.CurrentState == SessionState.Stopped,
            $"Expected Stopped at entry (stopAtEntry=true), got {session2.CurrentState}");

        var bps = session2.SetBreakpoints(reproSrc,
            (Line: 11, Column: null, Condition: null, HitCondition: null,
             Action: "break", CaptureScope: null, CaptureDepth: 0, CaptureExpressions: null));
        Console.WriteLine($"   Breakpoint line 11: verified={bps[0].Verified} message={bps[0].Message}");
        Assert(bps[0].Verified, $"Expected verified breakpoint, got: {bps[0].Message}");

        var stop = await session2.ContinueAndWaitAsync(timeoutSeconds: 10);
        Console.WriteLine($"   ContinueAndWait from entry stop -> status={stop.Status}, reason={stop.Reason}, line={stop.Line}");
        Assert(stop.Status == "stopped", $"Expected stopped at the breakpoint, got status={stop.Status}");
        Assert(stop.Line == 11, $"Expected stop at line 11, got {stop.Line}");
        Console.WriteLine("   ✅ PASS (entry stop → breakpoint flow)");
    }
    finally
    {
        try { session2.Disconnect(true); } catch { }
    }

    Console.WriteLine();
    Console.WriteLine("=== Step 3: launch with stopAtEntry=false → Running → continue waits ===");
    Console.WriteLine("   Regression (0.4.87): without stopAtEntry the session is Running");
    Console.WriteLine("   right after launch. debug_continue must WAIT for the next stop");
    Console.WriteLine("   instead of throwing 'Cannot continue: state is Running'.");

    using var session3 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    try
    {
        await session3.LaunchAsync(
            program: reproDll,
            cwd: Path.GetDirectoryName(reproDll),
            stopAtEntry: false);
        Console.WriteLine($"   State after launch: {session3.CurrentState}");
        Assert(session3.CurrentState == SessionState.Running,
            $"Expected Running after launch (stopAtEntry=false), got {session3.CurrentState}");

        var bps = session3.SetBreakpoints(reproSrc,
            (Line: 11, Column: null, Condition: null, HitCondition: null,
             Action: "break", CaptureScope: null, CaptureDepth: 0, CaptureExpressions: null));
        Console.WriteLine($"   Breakpoint line 11: verified={bps[0].Verified} message={bps[0].Message}");

        // No entry stop: the process resumes immediately, so the module may
        // not be loaded yet — the breakpoint starts pending and flips to
        // verified when ReproDebuggee.dll loads (BreakpointEvent sync).
        var verifyDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!bps[0].Verified && DateTime.UtcNow < verifyDeadline)
        {
            await Task.Delay(100);
            bps = session3.GetAllBreakpoints();
        }
        Console.WriteLine($"   After wait: verified={bps[0].Verified} message={bps[0].Message}");
        Assert(bps[0].Verified, $"Expected verified breakpoint, got: {bps[0].Message}");

        var stop = await session3.ContinueAndWaitAsync(timeoutSeconds: 10);
        Console.WriteLine($"   ContinueAndWait from Running -> status={stop.Status}, reason={stop.Reason}, line={stop.Line}");
        Assert(stop.Status == "stopped", $"Expected stopped at the breakpoint, got status={stop.Status}");
        Assert(stop.Line == 11, $"Expected stop at line 11, got {stop.Line}");
        Console.WriteLine("   ✅ PASS (continue-from-Running waits for the breakpoint)");
    }
    finally
    {
        try { session3.Disconnect(true); } catch { }
    }

    Console.WriteLine();
    Console.WriteLine("=== LAUNCH TEST COMPLETE ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
        Console.WriteLine($"   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    try { session.Disconnect(true); } catch { }
}
