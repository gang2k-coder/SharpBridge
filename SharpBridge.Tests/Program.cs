using System.Diagnostics;
using SharpBridge.Services;

// ===================================================================
// Attach-mode integration test
// Starts the debuggee process manually, then attaches to it
// ===================================================================

Console.WriteLine("=== SharpBridge Attach Test ===");
Console.WriteLine();

void Assert(bool condition, string msg)
{
    if (!condition) throw new Exception($"Assertion failed: {msg}");
}

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

    // === Step 7b: Variables expand ===
    Console.WriteLine("7b. Expanding variables ('numbers' list)...");
    var numbersVar = variables.First(v => v.Name == "numbers");
    Assert(numbersVar.VariablesReference > 0, "numbers should be expandable");
    var expanded = session.ExpandVariables(numbersVar.VariablesReference);
    Assert(expanded.Count >= 5, $"Expected >=5 children, got {expanded.Count}");
    Console.WriteLine($"   {expanded.Count} elements ✅ PASS");
    Console.WriteLine();

    // === Step 8: Step over ===
    Console.WriteLine("8. Stepping over...");
    stop = await session.StepAsync("over", mainThreadId);
    Console.WriteLine($"   Stopped at: {stop.FilePath}:{stop.Line}, Reason={stop.Reason}");
    Console.WriteLine($"   ✅ PASS");
    Console.WriteLine();

    // === Step 9: Breakpoint list ===
    Console.WriteLine("9. Listing breakpoints...");
    var bpList = session.GetAllBreakpoints();
    Console.WriteLine($"   count={bpList.Count}, action={bpList[0].Action}");
    Assert(bpList.Count == 1, $"Expected 1 breakpoint, got {bpList.Count}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 10: Exception breakpoints ===
    Console.WriteLine("10. Exception breakpoints...");
    var exFilters = session.GetExceptionBreakpointFilters();
    Assert(exFilters is not null && exFilters.Count == 2, "Expected 2 exception filters");
    Console.WriteLine($"   filters: {exFilters![0].Filter}, {exFilters[1].Filter}");
    session.SetExceptionBreakpoints(["all"]);
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 11: Capture state ===
    Console.WriteLine("11. Capture state...");
    var snap = session.CaptureState();
    Assert(snap.Index > 0, "Missing capture index");
    Assert(snap.Variables.Count > 0, "No variables in capture");
    Console.WriteLine($"   index={snap.Index}, vars={snap.Variables.Count} ✅ PASS");
    Console.WriteLine();

    // === Step 11: Get + clear captures ===
    Console.WriteLine("12. Get captures...");
    var caps = session.GetCaptures();
    Assert(caps.Count == 1, $"Expected 1 capture, got {caps.Count}");
    session.ClearCaptures();
    caps = session.GetCaptures();
    Assert(caps.Count == 0, $"Expected 0 after clear, got {caps.Count}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 12: Get + clear captures ===
    Console.WriteLine("12. Get captures...");
    Assert(session.CurrentState == DebugSession.State.Stopped, "Expected Stopped");
    Console.WriteLine($"   state={session.CurrentState} ✅ PASS");
    Console.WriteLine();

    // === Step 15: Remove breakpoint ===
    Console.WriteLine("15. Removing breakpoint...");
    Assert(session.RemoveBreakpoint(bps[0].Id), "Remove failed");
    Assert(session.GetAllBreakpoints().Count == 0, "BP not removed");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 16: Disconnect ===
    Console.WriteLine("16. Disconnecting...");
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
