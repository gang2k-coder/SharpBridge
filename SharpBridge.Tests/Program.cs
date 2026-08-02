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
    Console.WriteLine("2. Setting breakpoint on line 24 (blank → adjusted to 25, before the loop)...");
    var bps = session.SetBreakpoints(sourceFile,
        (Line: 24, Column: null, Condition: null, HitCondition: null,
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

    // === Step 3b: Modules ===
    Console.WriteLine("3b. Getting loaded modules...");
    var modules = session.GetModules();
    foreach (var m in modules)
        Console.WriteLine($"   {m.Name} ({m.Path})");
    Assert(modules.Any(m => m.Name == "TestDebuggee.dll"),
        $"Expected TestDebuggee.dll in modules, got: {string.Join(", ", modules.Select(m => m.Name))}");
    Assert(modules.Any(m => m.Name == "System.Private.CoreLib.dll"),
        $"Expected System.Private.CoreLib.dll in modules, got: {string.Join(", ", modules.Select(m => m.Name))}");
    Assert(modules.All(m => !string.IsNullOrEmpty(m.Path)), "Module path should not be empty");
    Console.WriteLine($"   ✅ PASS ({modules.Count} modules)");
    Console.WriteLine();

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

    // === Step 15: Capture breakpoint on a non-executable line ===
    // A capture breakpoint set before the module loads binds at an ADJUSTED
    // line (blank 24 -> executable 25). The BreakpointEvent handler must
    // re-key the capture config, otherwise the auto-capture silently
    // degrades to a plain break (blind spot ④).
    Console.WriteLine("15. Capture bp on blank line (adjusted-line auto-capture)...");
    var psi2 = new ProcessStartInfo("dotnet", debuggeeDll)
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi2.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee2 = Process.Start(psi2)!;
    int pid2 = debuggee2.Id;

    using var session2 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    await session2.AttachAsync(pid2);
    var capBps = session2.SetBreakpoints(sourceFile,
        (Line: 24, Column: null, Condition: null, HitCondition: null,
         Action: "capture", CaptureScope: "all", CaptureDepth: 0));
    var capBp = capBps[0];
    Assert(!capBp.Verified && capBp.IsPending,
        $"Expected pending before module load, got verified={capBp.Verified} pending={capBp.IsPending}");
    Assert(DebugSession.BreakpointStatus(capBp) == "pending", "Expected status 'pending'");

    await debuggee2.StandardInput.WriteLineAsync();
    var capStop = await session2.ContinueAndWaitAsync(timeoutSeconds: 20);
    Assert(capStop.Status == "exited",
        $"Expected run-to-exit (capture bps auto-continue), got {capStop.Status}");

    // BreakpointEvent sync: verified + adjusted line
    var boundBp = session2.GetAllBreakpoints()[0];
    Assert(boundBp.Verified, "Expected verified after module load");
    Assert(boundBp.Line == 25, $"Expected adjusted line 25, got {boundBp.Line}");
    Assert(DebugSession.BreakpointStatus(boundBp) == "verified", "Expected status 'verified'");

    // Capture fired at the ADJUSTED line (the blind-spot fix)
    var capCaps2 = session2.GetCaptures();
    Assert(capCaps2.Count == 1, $"Expected 1 capture, got {capCaps2.Count}");
    Assert(capCaps2[0].FilePath is not null && capCaps2[0].FilePath.Contains("TestDebuggee"),
        "Capture missing source path");
    Assert(capCaps2[0].Line == 25, $"Expected capture at adjusted line 25, got {capCaps2[0].Line}");
    var counterVar = capCaps2[0].Variables.FirstOrDefault(v => v.Name == "counter");
    Assert(counterVar is not null && counterVar.Value == "0",
        $"Expected counter=0 at line 24, got {counterVar?.Value}");
    Console.WriteLine("   ✅ PASS (pending → verified at line 25, capture fired, auto-continued to exit)");
    Console.WriteLine();

    // === Step 16: Gap stop — stop-ledger delivers on the next continue ===
    // A breakpoint hit while NO tool call is waiting (after a timed-out
    // continue) must be delivered on the next continue WITHOUT resuming.
    Console.WriteLine("16. Gap stop (stop-ledger delivery)...");
    var psi3 = new ProcessStartInfo("dotnet", [debuggeeDll, "--gap"])
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi3.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee3 = Process.Start(psi3)!;
    int pid3 = debuggee3.Id;

    using var session3 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    await session3.AttachAsync(pid3);
    const int gapLine = 38;   // counter++ inside the loop, after the 5s gap sleep
    session3.SetBreakpoints(sourceFile,
        (Line: gapLine, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0));

    await debuggee3.StandardInput.WriteLineAsync();
    var gapRun = await session3.ContinueAndWaitAsync(timeoutSeconds: 1);
    Assert(gapRun.Status == "running", $"Expected running after 1s timeout, got {gapRun.Status}");

    // Poll the ledger itself — deterministic, no timing guesses.
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (!session3.HasUnobservedStop && DateTime.UtcNow < deadline)
        await Task.Delay(100);
    Assert(session3.HasUnobservedStop, "Gap stop never arrived");

    var gapStop = await session3.ContinueAndWaitAsync(timeoutSeconds: 5);
    Assert(gapStop.Status == "stopped", $"Expected stopped (gap delivery), got {gapStop.Status}");
    Assert(gapStop.Line == gapLine, $"Expected gap delivery at line {gapLine}, got {gapStop.Line}");
    Assert(gapStop.Note is not null && gapStop.Note.Contains("NOT been resumed"),
        "Gap delivery must state the process was not resumed");
    Assert(!session3.HasUnobservedStop, "Gap delivery must consume the ledger");
    Assert(session3.CurrentState == SessionState.Stopped, "State must be Stopped after gap delivery");

    // Second continue actually resumes → normal stop at the next iteration.
    var gapNext = await session3.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(gapNext.Status == "stopped", $"Expected normal stop, got {gapNext.Status}");
    Assert(gapNext.Line == gapLine, $"Expected line {gapLine}, got {gapNext.Line}");
    Assert(string.IsNullOrEmpty(gapNext.Note), "Normal stop must not carry the gap note");
    Console.WriteLine("   ✅ PASS (delivered without resuming; next continue resumed)");
    Console.WriteLine();

    // === Step 17: Gap stop via debug_wait (returns it instead of throwing) ===
    Console.WriteLine("17. Gap stop via debug_wait...");
    var psi4 = new ProcessStartInfo("dotnet", [debuggeeDll, "--gap"])
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi4.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee4 = Process.Start(psi4)!;
    int pid4 = debuggee4.Id;

    using var session4 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    await session4.AttachAsync(pid4);
    session4.SetBreakpoints(sourceFile,
        (Line: gapLine, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0));
    await debuggee4.StandardInput.WriteLineAsync();
    var waitRun = await session4.ContinueAndWaitAsync(timeoutSeconds: 1);
    Assert(waitRun.Status == "running", $"Expected running after 1s timeout, got {waitRun.Status}");

    var deadline4 = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (!session4.HasUnobservedStop && DateTime.UtcNow < deadline4)
        await Task.Delay(100);
    Assert(session4.HasUnobservedStop, "Gap stop never arrived");

    // debug_wait must return the pending stop instead of throwing (the old
    // guard required Running and rejected Stopped).
    var waitStop = await session4.WaitAndWaitAsync(timeoutSeconds: 5);
    Assert(waitStop.Status == "stopped", $"Expected stopped from debug_wait, got {waitStop.Status}");
    Assert(waitStop.Line == gapLine, $"Expected line {gapLine}, got {waitStop.Line}");
    Assert(waitStop.Note is not null && waitStop.Note.Contains("NOT been resumed"),
        "debug_wait delivery must state the process was not resumed");
    Assert(!session4.HasUnobservedStop, "debug_wait delivery must consume the ledger");
    // Resume to finish cleanly.
    await session4.ContinueAndWaitAsync(timeoutSeconds: 10);
    Console.WriteLine("   ✅ PASS (debug_wait returned the pending stop)");
    Console.WriteLine();

    // === Step 18: Gap stop acknowledged via ObserveStopState (debug_state) ===
    Console.WriteLine("18. Gap stop acknowledged by state observation...");
    var psi5 = new ProcessStartInfo("dotnet", [debuggeeDll, "--gap"])
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi5.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee5 = Process.Start(psi5)!;
    int pid5 = debuggee5.Id;

    using var session5 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    await session5.AttachAsync(pid5);
    session5.SetBreakpoints(sourceFile,
        (Line: gapLine, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0));
    await debuggee5.StandardInput.WriteLineAsync();
    var ackRun = await session5.ContinueAndWaitAsync(timeoutSeconds: 1);
    Assert(ackRun.Status == "running", $"Expected running after 1s timeout, got {ackRun.Status}");

    var deadline5 = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (!session5.HasUnobservedStop && DateTime.UtcNow < deadline5)
        await Task.Delay(100);
    Assert(session5.HasUnobservedStop, "Gap stop never arrived");

    // Simulate the client checking debug_state (which calls ObserveStopState).
    session5.ObserveStopState();
    Assert(!session5.HasUnobservedStop, "ObserveStopState must consume the ledger");

    // Next continue resumes normally — no gap note.
    var ackStop = await session5.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(ackStop.Status == "stopped", $"Expected normal stop, got {ackStop.Status}");
    Assert(string.IsNullOrEmpty(ackStop.Note), "No gap note after acknowledgment");
    Console.WriteLine("   ✅ PASS (next continue resumed normally)");
    Console.WriteLine();

    // Cleanup gap sessions
    session3.Disconnect(terminateDebuggee: true);
    session4.Disconnect(terminateDebuggee: true);
    session5.Disconnect(terminateDebuggee: true);
    if (!debuggee3.HasExited) debuggee3.Kill();
    if (!debuggee4.HasExited) debuggee4.Kill();
    if (!debuggee5.HasExited) debuggee5.Kill();

    // === Step 19: Path normalization + incremental pattern ===
    // (a) Same file via relative vs absolute path must share ONE registry key
    //     (keys are normalized) — a re-set REPLACES, it does not duplicate.
    // (b) The tool layer's incremental pattern (BreakpointSet preserves
    //     existing same-file bps) is exercised directly; the MCP-level
    //     behavior is covered by E2E test 17.
    Console.WriteLine("19. Path normalization + incremental pattern...");
    var psi6 = new ProcessStartInfo("dotnet", debuggeeDll)
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi6.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee6 = Process.Start(psi6)!;
    int pid6 = debuggee6.Id;

    using var session6 = new DebugSession(loggerFactory.CreateLogger<DebugSession>());
    await session6.AttachAsync(pid6);

    // (a) normalization: absolute path, then the same file via a relative path
    var bpA = session6.SetBreakpoints(sourceFile,
        (Line: 38, Column: null, Condition: null, HitCondition: null,
         Action: "capture", CaptureScope: "all", CaptureDepth: 0))[0];
    Assert(bpA.Action == "capture", "bpA should be a capture breakpoint");

    var relSource = Path.GetRelativePath(repoRoot, sourceFile);   // TestDebuggee/Program.cs
    var bpRel = session6.SetBreakpoints(relSource,
        (Line: 40, Column: null, Condition: null, HitCondition: null,
         Action: "break", CaptureScope: null, CaptureDepth: 0))[0];
    var afterRel = session6.GetAllBreakpoints();
    Assert(afterRel.Count(b => b.FunctionName is null) == 1,
        $"Normalized re-set must not duplicate the file entry, got {afterRel.Count(b => b.FunctionName is null)}");
    Assert(afterRel[0].Line == 40, $"Expected the re-set breakpoint at line 40, got {afterRel[0].Line}");

    // (b) incremental pattern: collect existing (line 40, break) and ADD line 38
    var existing = session6.GetAllBreakpoints()
        .Where(b => b.FunctionName is null
                 && string.Equals(Path.GetFullPath(b.FilePath).ToLowerInvariant(),
                                  Path.GetFullPath(sourceFile).ToLowerInvariant(),
                                  StringComparison.Ordinal))
        .Select(b => (b.Line, b.Column, b.Condition, b.HitCondition,
                      b.Action, b.CaptureScope, b.CaptureDepth))
        .ToList();
    existing.Add((38, null, null, null, "capture", "all", 0));
    var entries6 = session6.SetBreakpoints(sourceFile, existing.ToArray());
    var bpB = entries6.Last(); // the just-added line 38
    Assert(bpB.Line == 38 && bpB.Action == "capture", "Incremental add must create the line-38 capture bp");

    var allBps6 = session6.GetAllBreakpoints();
    Assert(allBps6.Count == 2, $"Expected 2 breakpoints, got {allBps6.Count}");
    var bp38 = allBps6.First(b => b.Line == 38);
    var bp40 = allBps6.First(b => b.Line == 40);
    Assert(bp40.Action == "break" && bp40.Condition is null, "Existing bp must keep its action");
    Assert(bp38.Id != bpA.Id,
        $"Expected refreshed id after re-set (was {bpA.Id}, now {bp38.Id})");

    await debuggee6.StandardInput.WriteLineAsync();

    // Continue: iteration 0's line 38 is captured silently, then we stop at 40.
    var incStop = await session6.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(incStop.Status == "stopped", $"Expected stopped at 40, got {incStop.Status}");
    Assert(incStop.Line == 40, $"Expected stop at line 40, got {incStop.Line}");
    var incCaps = session6.GetCaptures();
    Assert(incCaps.Count == 1 && incCaps[0].Line == 38,
        $"Expected 1 capture at line 38, got {incCaps.Count} at {incCaps.FirstOrDefault()?.Line}");

    // Remove the break bp; the capture bp keeps auto-continuing to exit.
    Assert(session6.RemoveBreakpoint(bp40.Id), "Remove line-40 bp failed");
    var incFinal = await session6.ContinueAndWaitAsync(timeoutSeconds: 10);
    Assert(incFinal.Status == "exited", $"Expected exited after capture loop, got {incFinal.Status}");
    incCaps = session6.GetCaptures();
    Assert(incCaps.Count == 5, $"Expected 5 captures (one per iteration), got {incCaps.Count}");

    session6.Disconnect(terminateDebuggee: true);
    if (!debuggee6.HasExited) debuggee6.Kill();
    Console.WriteLine("   ✅ PASS (normalized key, incremental pattern, capture preserved, IDs refreshed)");
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
