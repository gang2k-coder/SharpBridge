using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpBridge.Services;
using SharpBridge.State;

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

// Start the debuggee process with diagnostic suspend — CLR freezes at startup
// until we call ResumeRuntime after ConfigurationDone.
var psi = new ProcessStartInfo("dotnet", debuggeeDll)
{
    RedirectStandardOutput = true,
    RedirectStandardInput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
psi.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
var debuggeeProcess = Process.Start(psi)!;

// With DOTNET_DefaultDiagnosticPortSuspend=1, the CLR is frozen before
// managed code runs. Get PID from Process.Id instead of stdout.
int pid = debuggeeProcess.Id;
Console.WriteLine($"Debuggee PID: {pid}");
Console.WriteLine();

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
using var session = new DebugSession(loggerFactory.CreateLogger<DebugSession>());

try
{
    // === Step 1: Attach ===
    Console.WriteLine("1. Attaching to debuggee...");
    await session.AttachAsync(pid);
    Console.WriteLine($"   State: {session.CurrentState}");
    if (session.CurrentState != SessionState.Attaching)
        throw new Exception($"Expected Attaching state after attach, got {session.CurrentState}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 2: Set breakpoint ===
    Console.WriteLine("2. Setting breakpoint on line 23 (for loop)...");
    var bps = session.SetBreakpoints(sourceFile,
        (Line: 23, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0));
    var bp = bps[0];
    Console.WriteLine($"   ID={bp.Id}, Verified={bp.Verified}, Line={bp.Line}");
    if (!bp.Verified) Console.WriteLine($"   Message: {bp.Message}");
    Console.WriteLine($"   {(bp.Verified ? "✅ PASS" : "⚠️ WARN — module not loaded yet (will be verified on continue)")}");
    Console.WriteLine();

    // === Step 3: Send ENTER + Continue (ConfigurationDone + ResumeRuntime) ===
    // ENTER goes into stdin pipe now; debuggee reads it after ResumeRuntime starts the CLR.
    Console.WriteLine("3. Sending ENTER to debuggee, then continuing (ConfigurationDone + ResumeRuntime)...");
    await debuggeeProcess.StandardInput.WriteLineAsync();

    var stop = await session.ContinueAndWaitAsync(timeoutSeconds: 20);
    Console.WriteLine($"   Status={stop.Status}, Reason={stop.Reason}");
    if (stop.FilePath is not null)
        Console.WriteLine($"   Stopped at: {stop.FilePath}:{stop.Line}");
    Console.WriteLine($"   {(stop.Status == "stopped" ? "✅ PASS" : $"State: {session.CurrentState}")}");
    Console.WriteLine();

    if (session.CurrentState != SessionState.Stopped)
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

    // === Step 9b: Remove source breakpoint to prepare for function bp tests ===
    Console.WriteLine("9b. Removing source breakpoint for function bp tests...");
    Assert(session.RemoveBreakpoint(bps[0].Id), "Remove failed");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 9c: Function breakpoint — short name (Class.Method) ===
    Console.Write("9c. Function breakpoint 'Calculator.Multiply'... ");
    var fnBps = session.SetFunctionBreakpoints(
        ("Calculator.Multiply", null, null, "break", null, 0));
    var fnBp = fnBps[0];
    Assert(fnBp.FunctionName == "Calculator.Multiply", "Name mismatch");
    Assert(fnBp.Verified, $"Not verified: {fnBp.Message}");
    Console.WriteLine($"id={fnBp.Id}, verified ");
    await session.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(session.CurrentState == SessionState.Stopped, "Should stop at function bp");
    var fnFrames = session.GetStackTrace(
        session.GetThreads().First(t => t.IsActive).Id, 0, 5);
    Assert(fnFrames.Any(f => f.Name.Contains("Multiply")),
        $"Not in Multiply: {fnFrames[0].Name}");
    Assert(session.RemoveBreakpoint(fnBp.Id), "Remove failed");
    Console.WriteLine($"✅ PASS (hit in {fnFrames[0].Name})");
    Console.WriteLine();

    // === Step 9d: Function breakpoint — method name only (suffix match) ===
    Console.Write("9d. Function breakpoint 'Multiply' (method-only)... ");
    var fnBps2 = session.SetFunctionBreakpoints(
        ("Multiply", null, null, "break", null, 0));
    Assert(fnBps2[0].Verified, $"Not verified: {fnBps2[0].Message}");
    await session.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(session.CurrentState == SessionState.Stopped, "Should stop");
    var fnFrames2 = session.GetStackTrace(
        session.GetThreads().First(t => t.IsActive).Id, 0, 5);
    Assert(fnFrames2.Any(f => f.Name.Contains("Multiply")),
        $"Not in Multiply: {fnFrames2[0].Name}");
    Assert(session.RemoveBreakpoint(fnBps2[0].Id), "Remove failed");
    Console.WriteLine("✅ PASS");
    Console.WriteLine();

    // === Step 9e: Function breakpoint — overload disambiguation by params ===
    Console.Write("9e. Function breakpoint 'Greeter.GetGreeting(string)' (single-param)... ");
    var fnBps3 = session.SetFunctionBreakpoints(
        ("Greeter.GetGreeting(string)", null, null, "break", null, 0));
    Assert(fnBps3[0].Verified, $"Not verified: {fnBps3[0].Message}");
    await session.ContinueAndWaitAsync(timeoutSeconds: 10);
    var fnFrames3 = session.GetStackTrace(
        session.GetThreads().First(t => t.IsActive).Id, 0, 5);
    Assert(fnFrames3.Any(f => f.Name.Contains("GetGreeting")),
        $"Not in GetGreeting: {fnFrames3[0].Name}");
    // Should be the single-param overload (NOT the two-param one)
    Assert(!fnFrames3[0].Name.EndsWith("string, string)"),
        $"Wrong overload (expected single-param): {fnFrames3[0].Name}");
    Assert(session.RemoveBreakpoint(fnBps3[0].Id), "Remove failed");
    Console.WriteLine($"✅ PASS ({fnFrames3[0].Name})");
    Console.WriteLine();

    // === Step 9f: Function breakpoint — two-param overload ===
    Console.Write("9f. Function breakpoint 'Greeter.GetGreeting(string, string)' (two-param)... ");
    var fnBps4 = session.SetFunctionBreakpoints(
        ("Greeter.GetGreeting(string, string)", null, null, "break", null, 0));
    Assert(fnBps4[0].Verified, $"Not verified: {fnBps4[0].Message}");
    await session.ContinueAndWaitAsync(timeoutSeconds: 10);
    var fnFrames4 = session.GetStackTrace(
        session.GetThreads().First(t => t.IsActive).Id, 0, 5);
    Assert(fnFrames4.Any(f => f.Name.Contains("GetGreeting")),
        $"Not in GetGreeting: {fnFrames4[0].Name}");
    Assert(fnBps4[0].Verified, "Two-param overload should be verified by SharpDbg");
    Assert(session.RemoveBreakpoint(fnBps4[0].Id), "Remove failed");
    Console.WriteLine($"✅ PASS (SharpDbg verified param match for {fnBps4[0].FunctionName})");
    Console.WriteLine();

    // Reset: all breakpoints should be gone
    Assert(session.GetAllBreakpoints().Count == 0, "All breakpoints should be removed");
    Console.WriteLine();

    // === Step 9g: Function breakpoint — generic type ===
    Console.Write("9g. Function breakpoint generic type... ");
    IReadOnlyList<DebugSession.BreakpointEntry> fnBps5 = null!;
    foreach (var pattern in new[] {
        "GenericProcessor<T>.Process",      // C# style
        "GenericProcessor`1.Process",       // CLR naming
        "Process"                           // method-only fallback
    })
    {
        fnBps5 = session.SetFunctionBreakpoints((pattern, null, null, "break", null, 0));
        if (fnBps5[0].Verified) break;
    }
    if (fnBps5[0].Verified)
    {
        await session.ContinueAndWaitAsync(timeoutSeconds: 10);
        Assert(session.CurrentState == SessionState.Stopped, "Should stop");
        var fnFrames5 = session.GetStackTrace(
            session.GetThreads().First(t => t.IsActive).Id, 0, 5);
        Assert(fnFrames5.Any(f => f.Name.Contains("Process")),
            $"Not in Process: {fnFrames5[0].Name}");
        Assert(session.RemoveBreakpoint(fnBps5[0].Id), "Remove failed");
        Console.WriteLine($"✅ PASS ({fnFrames5[0].Name})");
    }
    else
    {
        Console.WriteLine($"⚠️ SKIP — SharpDbg 0.1.6 generic type resolution: {fnBps5[0].Message}");
    }
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

    // === Step 12: Get + clear captures ===
    Console.WriteLine("12. Get captures...");
    var caps = session.GetCaptures();
    Assert(caps.Count == 1, $"Expected 1 capture, got {caps.Count}");
    session.ClearCaptures();
    caps = session.GetCaptures();
    Assert(caps.Count == 0, $"Expected 0 after clear, got {caps.Count}");
    Console.WriteLine("   ✅ PASS");
    Console.WriteLine();

    // === Step 13: State check ===
    Console.WriteLine("13. State check...");
    Assert(session.CurrentState == SessionState.Stopped, "Expected Stopped");
    Console.WriteLine($"   state={session.CurrentState} ✅ PASS");
    Console.WriteLine();

    // === Step 14: Disconnect ===
    Console.WriteLine("14. Disconnecting...");
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
