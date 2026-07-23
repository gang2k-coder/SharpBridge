using System.Diagnostics;
using SharpBridge.Services;

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

using var session = new DebugSession();
session.OnLog += msg => Console.WriteLine($"  LOG: {msg}");

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
    Console.WriteLine("=== LAUNCH TEST COMPLETE ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
        Console.WriteLine($"   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    try { session.Disconnect(true); } catch { }
}
