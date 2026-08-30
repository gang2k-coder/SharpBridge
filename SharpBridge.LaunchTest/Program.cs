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
        Console.WriteLine("   ROOT CAUSE: SharpDbg does NOT implement stopAtEntry.");
        Console.WriteLine("   After DebugActiveProcess, no stopped event is sent.");
        Console.WriteLine("   WaitForStopAsync hung waiting for an event that never comes.");
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
    Console.WriteLine("=== Step 2: debug_continue right after launch (state Running) ===");
    Console.WriteLine("   Regression: after launch the session is Running (adapter has no");
    Console.WriteLine("   stopAtEntry). debug_continue must WAIT for the next stop instead");
    Console.WriteLine("   of throwing 'Cannot continue: state is Running'.");

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
        Assert(session2.CurrentState == SessionState.Running,
            $"Expected Running after launch (no stopAtEntry), got {session2.CurrentState}");

        var bps = session2.SetBreakpoints(reproSrc,
            (Line: 11, Column: null, Condition: null, HitCondition: null,
             Action: "break", CaptureScope: null, CaptureDepth: 0));
        Console.WriteLine($"   Breakpoint line 11: verified={bps[0].Verified} message={bps[0].Message}");
        Assert(bps[0].Verified, $"Expected verified breakpoint, got: {bps[0].Message}");

        var stop = await session2.ContinueAndWaitAsync(timeoutSeconds: 10);
        Console.WriteLine($"   ContinueAndWait from Running -> status={stop.Status}, reason={stop.Reason}, line={stop.Line}");
        Assert(stop.Status == "stopped", $"Expected stopped at the breakpoint, got status={stop.Status}");
        Assert(stop.Line == 11, $"Expected stop at line 11, got {stop.Line}");
        Console.WriteLine("   ✅ PASS (continue-after-launch waits for the breakpoint)");
    }
    finally
    {
        try { session2.Disconnect(true); } catch { }
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
