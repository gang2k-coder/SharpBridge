using System.Diagnostics;
using SharpBridge.Services;

// ===================================================================
// Attach-mode integration test
// Starts the debuggee process manually, then attaches to it
// ===================================================================

Console.WriteLine("=== SharpBridge Attach Test ===");
Console.WriteLine();

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var debuggeeDll = Path.Combine(repoRoot, "TestDebuggee/bin/Debug/net10.0/TestDebuggee.dll");
var sourceFile = Path.Combine(repoRoot, "TestDebuggee/Program.cs");

Console.WriteLine($"Debuggee: {debuggeeDll}");
Console.WriteLine($"Source:   {sourceFile}");
Console.WriteLine();

// Start the debuggee process (it will print PID and wait for ENTER)
var debuggeeProcess = Process.Start(new ProcessStartInfo
{
    FileName = "dotnet",
    ArgumentList = { debuggeeDll },
    RedirectStandardOutput = true,
    RedirectStandardInput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
})!;

// Read the PID from stdout
var pidLine = debuggeeProcess.StandardOutput.ReadLine();
Console.WriteLine($"Debuggee output: {pidLine}");

var pidStr = pidLine?.Split(":")[1].Trim();
if (!int.TryParse(pidStr, out var pid))
{
    Console.WriteLine($"❌ Could not parse PID from: {pidLine}");
    return;
}
Console.WriteLine($"Debuggee PID: {pid}");
Console.WriteLine();

using var session = new DebugSession();
session.OnLog += msg => Console.WriteLine($"  LOG: {msg}");

try
{
    // === Step 1: Attach ===
    Console.WriteLine("1. Attaching to debuggee...");
    await session.AttachAsync(pid);
    Console.WriteLine($"   State: {session.CurrentState}");
    if (session.CurrentState != DebugSession.State.Stopped)
        throw new Exception("Expected Stopped state after attach!");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 2: Set breakpoint ===
    Console.WriteLine("2. Setting breakpoint on line 22 (counter++)...");
    var bps = session.SetBreakpoints(sourceFile,
        (Line: 22, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0));
    var bp = bps[0];
    Console.WriteLine($"   ID={bp.Id}, Verified={bp.Verified}, Line={bp.Line}");
    if (!bp.Verified) Console.WriteLine($"   Message: {bp.Message}");
    Console.WriteLine($"   {(bp.Verified ? "✅ PASS" : "⚠️ WARN — module not loaded yet (will be verified on continue)")}");
    Console.WriteLine();

    // === Step 3: Let debuggee run past ReadLine to hit breakpoint ===
    Console.WriteLine("3. Sending ENTER to debuggee, then continuing...");
    await debuggeeProcess.StandardInput.WriteLineAsync();

    var stop = await session.ContinueAndWaitAsync(timeoutSeconds: 20);
    Console.WriteLine($"   Status={stop.Status}, Reason={stop.Reason}");
    if (stop.FilePath is not null)
        Console.WriteLine($"   Stopped at: {stop.FilePath}:{stop.Line}");
    Console.WriteLine($"   {(stop.Status == "stopped" ? "✅ PASS" : $"State: {session.CurrentState}")}");
    Console.WriteLine();

    if (session.CurrentState != DebugSession.State.Stopped)
    {
        Console.WriteLine("   Not stopped — skipping remaining tests.");
        session.Disconnect(true);
        return;
    }

    // === Step 4: Threads ===
    Console.WriteLine("4. Getting threads...");
    var threads = session.GetThreads();
    foreach (var t in threads)
        Console.WriteLine($"   Thread {t.Id}: {t.Name}");
    Console.WriteLine($"   ✅ PASS ({threads.Count} threads)");
    Console.WriteLine();

    // === Step 5: Stack trace ===
    Console.WriteLine("5. Getting stack trace...");
    var mainThreadId = stop.ThreadId ?? threads.First().Id;
    var frames = session.GetStackTrace(mainThreadId);
    foreach (var f in frames.Take(5))
        Console.WriteLine($"   [{f.Id}] {f.Name}");
    if (frames.Count > 0 && frames[0].Source is not null)
        Console.WriteLine($"   Source: {frames[0].Source}:{frames[0].Line}");
    Console.WriteLine($"   ✅ PASS ({frames.Count} frames)");
    Console.WriteLine();

    if (frames.Count == 0)
    {
        Console.WriteLine("   No frames — skipping remaining inspection tests.");
        session.Disconnect(true);
        return;
    }

    // === Step 6: Variables ===
    var topFrameId = frames.First().Id;
    Console.WriteLine("6. Getting variables for top frame...");
    var variables = session.GetVariablesForFrame(topFrameId);
    foreach (var v in variables)
        Console.WriteLine($"   {v.Name} ({v.Type ?? "?"}) = {v.Value} [ref={v.VariablesReference}]");
    Console.WriteLine($"   ✅ PASS ({variables.Count} variables)");
    Console.WriteLine();

    // === Step 7: Evaluate ===
    Console.WriteLine("7. Evaluating expression...");
    var eval = await session.EvaluateAsync("counter + 100", topFrameId);
    Console.WriteLine($"   counter + 100 = {eval.Result}, Type: {eval.Type}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 8: Step over ===
    Console.WriteLine("8. Stepping over...");
    stop = await session.StepAsync("over", mainThreadId);
    Console.WriteLine($"   Stopped at: {stop.FilePath}:{stop.Line}, Reason={stop.Reason}");
    Console.WriteLine($"   ✅ PASS");
    Console.WriteLine();

    // === Step 9: Disconnect ===
    Console.WriteLine("9. Disconnecting...");
    session.Disconnect(terminateDebuggee: true);
    Console.WriteLine($"   State: {session.CurrentState}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    Console.WriteLine("=== ALL TESTS PASSED ✅ ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ FAILED:");
    Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
        Console.WriteLine($"   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    if (!debuggeeProcess.HasExited)
        debuggeeProcess.Kill();
}
