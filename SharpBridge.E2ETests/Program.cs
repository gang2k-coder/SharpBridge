using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

Console.WriteLine("=== SharpBridge E2E Tests ===\n");

var tests = 0;
var passed = 0;

var serverProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SharpBridge"));
var debuggeeProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestDebuggee"));
var debuggeeDll = Path.Combine(debuggeeProj, "bin/Debug/net10.0/TestDebuggee.dll");

// Build
var buildPsi = new ProcessStartInfo("dotnet", ["build", serverProj, "-q"])
{ RedirectStandardOutput = true, RedirectStandardError = true };
(Process.Start(buildPsi)!).WaitForExit();
buildPsi = new ProcessStartInfo("dotnet", ["build", debuggeeProj, "-q"])
{ RedirectStandardOutput = true, RedirectStandardError = true };
(Process.Start(buildPsi)!).WaitForExit();

// Start debuggee
var debuggee = Process.Start(new ProcessStartInfo
{
    FileName = "dotnet", ArgumentList = { debuggeeDll },
    RedirectStandardOutput = true, RedirectStandardInput = true,
    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
})!;
var pidLine = await debuggee.StandardOutput.ReadLineAsync();
var pid = int.Parse(pidLine!.Split(":")[1].Trim());
Console.WriteLine($"Debuggee PID: {pid}\n");

try
{
    // Start MCP server
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = ["run", "--project", serverProj, "--no-build"],
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
        .EnumerateArray().First(v => v.GetProperty("Name").GetString() == "counter")
        .GetProperty("Value").GetString();
    Assert(counterVal == "0", $"Expected counter=0, got {counterVal}");
    Console.WriteLine($"   counter={counterVal} ✅");

    // Cleanup
    await client.CallToolAsync("debug_disconnect", new Dictionary<string, object?> { ["terminateDebuggee"] = true });
    Console.WriteLine($"\n=== {passed}/{tests} PASSED ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null) Console.WriteLine($"   Inner: {ex.InnerException.Message}");
}
finally
{
    if (!debuggee.HasExited) debuggee.Kill();
}

void Assert(bool condition, string msg)
{
    if (!condition) throw new Exception($"Assertion failed: {msg}");
}
