using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

Console.WriteLine("=== SharpBridge E2E Tests ===\n");

var tests = 0;
var passed = 0;

var serverProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SharpBridge"));
var debuggeeProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestDebuggee"));

// Build into an isolated artifacts directory: the C# extension in VS Code
// (Roslyn / Dev Kit design-time builds) touches the default obj/ dirs, which
// intermittently races with our child builds (MSB3492 lock errors, "target is
// being built fully"). A dedicated artifacts path has zero overlap with it.
var e2eArtifacts = Path.Combine(Path.GetTempPath(), "sharpbridge-e2e-artifacts");

// Run a child dotnet build. MSBuild server / node reuse are disabled to avoid
// lingering processes; a single retry covers transient lock hiccups.
static (int ExitCode, string Output) RunBuild(string project, string artifactsDir)
{
    var psi = new ProcessStartInfo("dotnet", ["build", project, "-v", "m", "--artifacts-path", artifactsDir])
    {
        RedirectStandardOutput = true, RedirectStandardError = true,
        Environment = { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1" }
    };
    using var proc = Process.Start(psi)!;
    proc.WaitForExit();
    return (proc.ExitCode, proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd());
}

void BuildOrThrow(string project, string label)
{
    var (code, output) = RunBuild(project, e2eArtifacts);
    if (code == 0) return;

    Thread.Sleep(1000); // retry once — transient lock
    (code, output) = RunBuild(project, e2eArtifacts);
    if (code != 0)
        throw new Exception($"{label} build failed:\n{output}");
}

// Build
BuildOrThrow(serverProj, "Server");
BuildOrThrow(debuggeeProj, "Debuggee");

// Resolve outputs — the artifacts layout lower-cases the configuration
// directory (bin/<Project>/debug/net10.0), so search by file name instead
// of hard-coding the casing.
static string FindOutputDll(string artifactsDir, string projectName)
{
    var dll = Directory.GetFiles(Path.Combine(artifactsDir, "bin", projectName), "*.dll", SearchOption.AllDirectories)
        .FirstOrDefault(f => Path.GetFileName(f) == projectName + ".dll");
    return dll ?? throw new Exception($"Build output not found for {projectName} under {artifactsDir}");
}

var serverDll = FindOutputDll(e2eArtifacts, "SharpBridge");
var debuggeeDll = FindOutputDll(e2eArtifacts, "TestDebuggee");

// Start debuggee with diagnostic suspend — CLR freezes until ResumeRuntime
var psi = new ProcessStartInfo("dotnet", [debuggeeDll])
{
    RedirectStandardOutput = true, RedirectStandardInput = true,
    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
};
psi.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
var debuggee = Process.Start(psi)!;
int pid = debuggee.Id;
Console.WriteLine($"Debuggee PID: {pid}\n");

try
{
    // Start MCP server
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = [serverDll],
        WorkingDirectory = serverProj
    });
    await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
    {
        ClientInfo = new() { Name = "E2ETest", Version = "1.0" },
        Capabilities = new ClientCapabilities()
    });

    static string GetText(CallToolResult r)
    {
        if (r.IsError == true) throw new Exception($"MCP error: {((TextContentBlock)r.Content[0]).Text}");
        return ((TextContentBlock)r.Content[0]).Text;
    }

    // Test 1: List tools
    tests++; passed++;
    Console.WriteLine("1. List tools...");
    var tools = await client.ListToolsAsync();
    Assert(tools.Count >= 20, $"Expected >=20 tools, got {tools.Count}");
    Console.WriteLine($"   {tools.Count} tools ✅");

    // Test 2: Attach
    tests++; passed++;
    Console.WriteLine("2. Attach...");
    var attachJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_attach", new Dictionary<string, object?> { ["processId"] = pid })));
    Assert(attachJson.RootElement.GetProperty("status").GetString() == "attached", "Attach failed");
    var attachedPid = attachJson.RootElement.GetProperty("processId").GetInt32();
    await client.CallToolAsync("debug_select", new Dictionary<string, object?> { ["processId"] = attachedPid });
    Console.WriteLine($"   PID {attachedPid} ✅");

    // Test 3: Breakpoint + Continue
    tests++; passed++;
    Console.WriteLine("3. Breakpoint + continue...");
    var sourceFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../TestDebuggee/Program.cs"));
    await client.CallToolAsync("breakpoint_set",
        new Dictionary<string, object?> { ["filePath"] = sourceFile, ["line"] = 23 });
    await debuggee.StandardInput.WriteLineAsync();
    var contJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
    Assert(contJson.RootElement.GetProperty("status").GetString() == "stopped", "Not stopped");
    var threadId = contJson.RootElement.GetProperty("threadId").GetInt32();
    Assert(!debuggee.HasExited, "Debuggee exited during continue");
    // The continue response must carry the real hit location now
    var hasStopSource = contJson.RootElement.TryGetProperty("source", out var stopSource)
        && stopSource.GetProperty("path").GetString()?.Contains("TestDebuggee") == true;
    Assert(hasStopSource, "Continue response missing hit source location");
    // The breakpoint set while the module wasn't loaded must now be verified,
    // with the line adjusted from the blank 23 to the executable line 24.
    var bpCheckJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    Assert(bpCheckJson.RootElement.GetProperty("count").GetInt32() == 1, "Expected 1 BP after continue");
    var bpCheck = bpCheckJson.RootElement.GetProperty("breakpoints")[0];
    Assert(bpCheck.GetProperty("status").GetString() == "verified",
        $"Expected verified after module load, got {bpCheck.GetProperty("status").GetString()}");
    Assert(bpCheck.GetProperty("line").GetInt32() == 24,
        $"Expected adjusted line 24, got {bpCheck.GetProperty("line").GetInt32()}");
    Console.WriteLine($"   threadId={threadId}, alive ✅");

    // Test 4: Stack + Variables
    tests++; passed++;
    Console.WriteLine("4. Stack + variables...");
    var stackJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("stacktrace_get", new Dictionary<string, object?> { ["threadId"] = threadId })));
    Assert(stackJson.RootElement.GetProperty("count").GetInt32() > 0, "No frames");
    var frameId = stackJson.RootElement.GetProperty("frames")[0].GetProperty("id").GetInt32();

    var varsJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("variables_get", new Dictionary<string, object?> { ["frameId"] = frameId })));
    var counterVal = varsJson.RootElement.GetProperty("variables")
        .EnumerateArray().First(v => v.GetProperty("name").GetString() == "counter")
        .GetProperty("value").GetString();
    Assert(counterVal == "0", $"Expected counter=0, got {counterVal}");
    Console.WriteLine($"   counter={counterVal} ✅");

    // Test 4b: Evaluate (still in Main scope with counter variable)
    tests++; passed++;
    Console.WriteLine("4b. Evaluate...");
    var evalJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("evaluate", new Dictionary<string, object?> { ["expression"] = "counter + 100", ["frameId"] = frameId, ["processId"] = attachedPid })));
    Assert(evalJson.RootElement.GetProperty("type").GetString() == "int", "Expected int type");
    Console.WriteLine($"   counter+100={evalJson.RootElement.GetProperty("result").GetString()} ✅");

    // Test 4c: Variables expand (use numbers variable from Main scope)
    tests++; passed++;
    Console.WriteLine("4c. Variables expand...");
    var varsExResp = await client.CallToolAsync("variables_get",
        new Dictionary<string, object?> { ["frameId"] = frameId, ["scope"] = "locals" });
    var varsExJson = JsonDocument.Parse(GetText(varsExResp));
    var numbersVar2 = varsExJson.RootElement.GetProperty("variables").EnumerateArray()
        .First(v => v.GetProperty("name").GetString() == "numbers");
    Assert(numbersVar2.GetProperty("variablesReference").GetInt32() > 0, "numbers not expandable");
    var expandJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("variables_expand",
            new Dictionary<string, object?> { ["variablesReference"] = numbersVar2.GetProperty("variablesReference").GetInt32() })));
    Assert(expandJson.RootElement.GetProperty("count").GetInt32() >= 5, $"Expected >=5 children, got {expandJson.RootElement.GetProperty("count").GetInt32()}");
    Console.WriteLine($"   {expandJson.RootElement.GetProperty("count").GetInt32()} children ✅");

    // Test 5: Breakpoint list + remove
    tests++; passed++;
    Console.WriteLine("5. Breakpoint list/remove...");
    var bpListJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    Assert(bpListJson.RootElement.GetProperty("count").GetInt32() == 1, "Expected 1 BP");
    var bpId = bpListJson.RootElement.GetProperty("breakpoints")[0].GetProperty("id").GetInt32();
    var rmJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_remove", new Dictionary<string, object?> { ["id"] = bpId })));
    Assert(rmJson.RootElement.GetProperty("removed").GetBoolean(), "Remove failed");
    // Verify source breakpoint is truly gone
    var afterRmJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    Assert(afterRmJson.RootElement.GetProperty("count").GetInt32() == 0, "Source BP not removed");
    Console.WriteLine("   ✅");

    // Test 5b: Function breakpoint — short name via MCP
    tests++; passed++;
    Console.WriteLine("5b. Function breakpoint (Calculator.Multiply)...");
    var fnBpJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("function_breakpoint_set",
            new Dictionary<string, object?> { ["functionName"] = "Calculator.Multiply" })));
    Assert(fnBpJson.RootElement.GetProperty("verified").GetBoolean(), "Should be verified");
    var fnBpId = fnBpJson.RootElement.GetProperty("id").GetInt32();
    Assert(fnBpJson.RootElement.GetProperty("functionName").GetString() == "Calculator.Multiply", "Name mismatch");
    // Continue to hit it
    var fnContJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
    var fnContStatus = fnContJson.RootElement.GetProperty("status").GetString();
    Assert(fnContStatus == "stopped", $"Not stopped (status={fnContStatus})");
    // Verify it's in the breakpoint list — find by functionName, not index
    var fnListJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    Assert(fnListJson.RootElement.GetProperty("count").GetInt32() >= 1, "No bps in list");
    var fnBpInList = fnListJson.RootElement.GetProperty("breakpoints").EnumerateArray()
        .FirstOrDefault(bp => bp.TryGetProperty("functionName", out var fn) && fn.GetString() == "Calculator.Multiply");
    Assert(fnBpInList.ValueKind != JsonValueKind.Undefined, "Function breakpoint not found in list");
    // Remove
    await client.CallToolAsync("breakpoint_remove", new Dictionary<string, object?> { ["id"] = fnBpId });
    // Verify removed
    var afterFnRmJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    Assert(afterFnRmJson.RootElement.GetProperty("count").GetInt32() == 0, "Fn BP not removed");
    Console.WriteLine("   ✅");

    // Test 5c: Function breakpoint — parameter matching via MCP
    tests++; passed++;
    Console.WriteLine("5c. Function breakpoint (Greeter.GetGreeting(string))...");
    var fn2Json = JsonDocument.Parse(GetText(
        await client.CallToolAsync("function_breakpoint_set",
            new Dictionary<string, object?> { ["functionName"] = "Greeter.GetGreeting(string)" })));
    Assert(fn2Json.RootElement.GetProperty("verified").GetBoolean(), "Should be verified");
    var fn2Id = fn2Json.RootElement.GetProperty("id").GetInt32();
    var fn2ContJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
    Assert(fn2ContJson.RootElement.GetProperty("status").GetString() == "stopped", "Not stopped");
    await client.CallToolAsync("breakpoint_remove", new Dictionary<string, object?> { ["id"] = fn2Id });
    Console.WriteLine("   ✅");

    // Test 5d: Function breakpoint — two-param overload
    tests++; passed++;
    Console.WriteLine("5d. Function breakpoint (Greeter.GetGreeting(string, string))...");
    var fn3Json = JsonDocument.Parse(GetText(
        await client.CallToolAsync("function_breakpoint_set",
            new Dictionary<string, object?> { ["functionName"] = "Greeter.GetGreeting(string, string)" })));
    Assert(fn3Json.RootElement.GetProperty("verified").GetBoolean(), "Should be verified");
    var fn3Id = fn3Json.RootElement.GetProperty("id").GetInt32();
    var fn3ContJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
    Assert(fn3ContJson.RootElement.GetProperty("status").GetString() == "stopped", "Not stopped");
    await client.CallToolAsync("breakpoint_remove", new Dictionary<string, object?> { ["id"] = fn3Id });
    Console.WriteLine("   ✅");

    // Test 5e: Function breakpoint — generic type
    tests++; passed++;
    Console.WriteLine("5e. Function breakpoint (GenericProcessor<T>.Process)... ");
    IReadOnlyDictionary<string, object?> fn4Args;
    bool fn4Ok = false;
    foreach (var pattern in new[] { "GenericProcessor<T>.Process", "GenericProcessor`1.Process", "Process" })
    {
        var fn4Json = JsonDocument.Parse(GetText(
            await client.CallToolAsync("function_breakpoint_set",
                new Dictionary<string, object?> { ["functionName"] = pattern })));
        if (fn4Json.RootElement.GetProperty("verified").GetBoolean())
        {
            fn4Args = new Dictionary<string, object?> { ["id"] = fn4Json.RootElement.GetProperty("id").GetInt32() };
            var fn4ContJson = JsonDocument.Parse(GetText(
                await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
            Assert(fn4ContJson.RootElement.GetProperty("status").GetString() == "stopped",
                $"Hit with pattern '{pattern}' but didn't stop");
            await client.CallToolAsync("breakpoint_remove", fn4Args);
            fn4Ok = true;
            break;
        }
    }
    Assert(fn4Ok, "No generic pattern verified");
    Console.WriteLine("✅");

    // Test 5f: Function breakpoint — multi-bind (method name only, matches Calculator.Multiply)
    tests++; passed++;
    Console.WriteLine("5f. Function breakpoint multi-bind ('Multiply')...");
    var fn5Json = JsonDocument.Parse(GetText(
        await client.CallToolAsync("function_breakpoint_set",
            new Dictionary<string, object?> { ["functionName"] = "Multiply" })));
    Assert(fn5Json.RootElement.GetProperty("verified").GetBoolean(), "Should be verified");
    var fn5Id = fn5Json.RootElement.GetProperty("id").GetInt32();
    var fn5ContJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 20 })));
    Assert(fn5ContJson.RootElement.GetProperty("status").GetString() == "stopped", "Not stopped");
    await client.CallToolAsync("breakpoint_remove", new Dictionary<string, object?> { ["id"] = fn5Id });
    Console.WriteLine("   ✅");

    // Test 6: Exception breakpoints
    tests++; passed++;
    Console.WriteLine("6. Exception breakpoints...");
    var exJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("exception_breakpoints", new Dictionary<string, object?> { ["action"] = "list" })));
    Assert(exJson.RootElement.GetProperty("count").GetInt32() == 2, "Expected 2 filters");
    await client.CallToolAsync("exception_breakpoints",
        new Dictionary<string, object?> { ["action"] = "set", ["filters"] = new[] { "all" } });
    Console.WriteLine("   ✅");

    // Test 7: Capture state + get_captures + clear_captures
    tests++; passed++;
    Console.WriteLine("7. Capture state...");
    var capJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("capture_state", new Dictionary<string, object?>())));
    Assert(capJson.RootElement.GetProperty("index").GetInt32() > 0, "No capture index");
    var capsJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("get_captures", new Dictionary<string, object?>())));
    Assert(capsJson.RootElement.GetProperty("count").GetInt32() == 1, "Expected 1 capture");
    await client.CallToolAsync("clear_captures", new Dictionary<string, object?>());
    var clearedJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("get_captures", new Dictionary<string, object?>())));
    Assert(clearedJson.RootElement.GetProperty("count").GetInt32() == 0, "Clear failed");
    Console.WriteLine("   ✅");

    // Test 8: Debug state
    tests++; passed++;
    Console.WriteLine("8. Debug state...");
    var stateJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_state", new Dictionary<string, object?>())));
    Assert(stateJson.RootElement.GetProperty("state").GetString() == "Stopped", "Not Stopped");
    Console.WriteLine("   ✅");

    // Test 9: Debug step — in
    tests++; passed++;
    Console.WriteLine("9. Step in...");
    var stepInJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_step", new Dictionary<string, object?> { ["type"] = "in" })));
    Assert(stepInJson.RootElement.GetProperty("status").GetString() == "stopped", "Step in failed");
    Console.WriteLine("   ✅");

    // Test 10: Exception info
    tests++; passed++;
    Console.WriteLine("10. Exception info...");
    var exInfoJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("exception_info", new Dictionary<string, object?>())));
    Assert(exInfoJson.RootElement.GetProperty("hasException").GetBoolean() == false, "Unexpected exception");
    Console.WriteLine("   ✅");

    // Test 11: Capture-action breakpoint — auto-capture without stopping
    // Uses a fresh debuggee: one capture breakpoint on the loop's counter++
    // line (fires once per iteration, silently) plus a plain breakpoint in a
    // SECOND source file (LoopEnd.cs) so we can stop and read the snapshots
    // before the process exits. (Source breakpoints replace per-file, so a
    // second breakpoint in Program.cs would wipe the capture one.)
    tests++; passed++;
    Console.WriteLine("11. Capture breakpoint (auto-capture)...");
    var psi3 = new ProcessStartInfo("dotnet", [debuggeeDll])
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi3.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee3 = Process.Start(psi3)!;
    var pid3 = debuggee3.Id;
    var attach3Json = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_attach", new Dictionary<string, object?> { ["processId"] = pid3 })));
    Assert(attach3Json.RootElement.GetProperty("status").GetString() == "attached", "Attach #3 failed");
    await client.CallToolAsync("debug_select", new Dictionary<string, object?> { ["processId"] = pid3 });

    const int counterLine = 29;   // counter++ inside the loop
    var capSetJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_set", new Dictionary<string, object?>
        {
            ["filePath"] = sourceFile, ["line"] = counterLine, ["action"] = "capture"
        })));
    Assert(capSetJson.RootElement.GetProperty("action").GetString() == "capture", "Capture action not set");
    var actualCounterLine = capSetJson.RootElement.GetProperty("line").GetInt32();
    // Stop after the loop via a breakpoint in a different source file
    // (LoopEnd.Signal) — set while the module is not yet loaded, so it binds
    // when the module loads (pending-breakpoint rebinding).
    var loopEndFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../TestDebuggee/LoopEnd.cs"));
    const int loopEndLine = 8;   // GC.KeepAlive(0); inside LoopEnd.Signal()
    await client.CallToolAsync("breakpoint_set", new Dictionary<string, object?>
    {
        ["filePath"] = loopEndFile, ["line"] = loopEndLine
    });
    // Note: at attach time the module's symbols may not be loaded yet, so the
    // adapter reports breakpoints as pending (unverified); they bind when the
    // module loads. The functional assertions below are the real verification.

    // Both breakpoints must be reported as pending while the module is unloaded.
    var pendingListJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    var pendingBps = pendingListJson.RootElement.GetProperty("breakpoints").EnumerateArray().ToList();
    Assert(pendingBps.Count == 2, $"Expected 2 breakpoints, got {pendingBps.Count}");
    Assert(pendingBps.All(b => b.GetProperty("status").GetString() == "pending"),
        $"Expected all breakpoints pending, got {string.Join(",", pendingBps.Select(b => b.GetProperty("status").GetString()))}");

    await debuggee3.StandardInput.WriteLineAsync();
    var capContJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_continue", new Dictionary<string, object?> { ["timeout"] = 30 })));
    Assert(capContJson.RootElement.GetProperty("status").GetString() == "stopped",
        $"Expected stopped after the capture loop, got {capContJson.RootElement.GetProperty("status").GetString()}");
    // The continue response must carry the real hit location now
    var capStopLine = capContJson.RootElement.GetProperty("source").GetProperty("line").GetInt32();
    Assert(capStopLine == loopEndLine, $"Expected stop at line {loopEndLine}, got {capStopLine}");

    // BreakpointEvent sync: both breakpoints must have flipped to verified.
    var verifiedListJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("breakpoint_list", new Dictionary<string, object?>())));
    var verifiedBps = verifiedListJson.RootElement.GetProperty("breakpoints").EnumerateArray().ToList();
    Assert(verifiedBps.Count == 2, $"Expected 2 breakpoints after module load, got {verifiedBps.Count}");
    Assert(verifiedBps.All(b => b.GetProperty("status").GetString() == "verified"),
        $"Expected all breakpoints verified, got {string.Join(",", verifiedBps.Select(b => b.GetProperty("status").GetString()))}");

    var capCapsJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("get_captures", new Dictionary<string, object?>())));
    var capList = capCapsJson.RootElement.GetProperty("captures");
    Assert(capCapsJson.RootElement.GetProperty("count").GetInt32() == 5,
        $"Expected 5 captures (one per loop iteration), got {capCapsJson.RootElement.GetProperty("count").GetInt32()}");
    var counters = capList.EnumerateArray().Select(c =>
    {
        Assert(c.GetProperty("source").GetProperty("path").GetString()!.Contains("TestDebuggee"),
            "Capture snapshot missing source path");
        Assert(c.GetProperty("source").GetProperty("line").GetInt32() == actualCounterLine,
            $"Capture snapshot wrong line: {c.GetProperty("source").GetProperty("line").GetInt32()}");
        return c.GetProperty("variables").EnumerateArray()
            .First(v => v.GetProperty("name").GetString() == "counter")
            .GetProperty("value").GetString();
    }).ToList();
    Assert(counters.SequenceEqual(new[] { "0", "1", "2", "3", "4" }),
        $"Expected counters 0..4 (breakpoint fires BEFORE the statement runs), got {string.Join(",", counters)}");
    await client.CallToolAsync("debug_disconnect",
        new Dictionary<string, object?> { ["terminateDebuggee"] = true, ["processId"] = pid3 });
    Console.WriteLine($"   counters={string.Join(",", counters)} ✅");

    // Test 12: session context back to original after operations
    tests++; passed++;
    Console.WriteLine("12. Session context preserved...");
    var stateCheckJson = JsonDocument.Parse(GetText(
        await client.CallToolAsync("debug_state", new Dictionary<string, object?> { ["processId"] = attachedPid })));
    Assert(stateCheckJson.RootElement.GetProperty("state").GetString() == "Stopped", "Not Stopped");
    Console.WriteLine("   ✅");

    // Test 13: Filter rejection — call Stopped-only tool in Attaching state.
    // Disconnect the first session first: SharpDbg only supports one adapter per process.
    tests++; passed++;
    Console.WriteLine("13. Filter rejection (stacktrace_get requires Stopped)...");
    await client.CallToolAsync("debug_disconnect", new Dictionary<string, object?> { ["terminateDebuggee"] = true, ["processId"] = attachedPid });
    var psi2 = new ProcessStartInfo("dotnet", [debuggeeDll])
    {
        RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
    };
    psi2.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    using var debuggee2 = Process.Start(psi2)!;
    int pid2 = debuggee2.Id;
    await client.CallToolAsync("debug_attach", new Dictionary<string, object?> { ["processId"] = pid2 });
    await client.CallToolAsync("debug_select", new Dictionary<string, object?> { ["processId"] = pid2 });
    try
    {
        GetText(await client.CallToolAsync("stacktrace_get", new Dictionary<string, object?> { ["threadId"] = 1 }));
        throw new Exception("Should have been rejected by filter!");
    }
    catch (Exception ex) when (ex.Message.Contains("requires session state"))
    {
        Console.WriteLine($"   Rejected as expected ✅");
    }
    await client.CallToolAsync("debug_disconnect", new Dictionary<string, object?> { ["terminateDebuggee"] = true });
    if (!debuggee2.HasExited) debuggee2.Kill();

    // Test 14: Filter rejection — no session selected
    tests++; passed++;
    Console.WriteLine("14. Filter rejection (no session)...");
    // Disconnect attachedPid first to clear session
    await client.CallToolAsync("debug_disconnect", new Dictionary<string, object?> { ["terminateDebuggee"] = true, ["processId"] = attachedPid });
    try
    {
        GetText(await client.CallToolAsync("stacktrace_get", new Dictionary<string, object?> { ["threadId"] = 1 }));
        throw new Exception("Should have been rejected!");
    }
    catch (Exception ex) when (ex.Message.Contains("No debug session"))
    {
        Console.WriteLine($"   Rejected as expected ✅");
    }

    Console.WriteLine($"\n=== {passed}/{tests} PASSED ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null) Console.WriteLine($"   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    if (!debuggee.HasExited) debuggee.Kill();
}

void Assert(bool condition, string msg)
{
    if (!condition) throw new Exception($"Assertion failed: {msg}");
}
