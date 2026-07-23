// Test debuggee for SharpBridge — a simple program to exercise debugging features

using System.Diagnostics;

// Check for --throw mode
bool throwMode = args.Length > 0 && args[0] == "--throw";

// Show PID for attach and pause to give time to connect
Console.WriteLine($"PID: {Environment.ProcessId}");
Console.WriteLine("Press ENTER to start...");
Console.ReadLine();

Console.WriteLine("=== SharpBridge Test Debuggee ===");

// Some local variables to inspect
int counter = 0;
string message = "Hello from debuggee!";
var numbers = new List<int> { 10, 20, 30, 40, 50 };

Console.WriteLine($"Starting loop with {numbers.Count} items...");

// Loop — set breakpoints here to test continue/step
for (int i = 0; i < numbers.Count; i++)
{
    counter++;
    int current = numbers[i];
    int doubled = DoubleValue(current); // Step into this method
    Console.WriteLine($"  [{i}] {current} * 2 = {doubled}, counter = {counter}");

    // In --throw mode, throw during the 3rd iteration
    if (throwMode && i == 2)
    {
        Console.WriteLine("About to throw test exception...");
        throw new InvalidOperationException("Test exception from debuggee");
    }
}

Console.WriteLine($"\nFinal counter: {counter}");
Console.WriteLine($"Message: {message}");
Console.WriteLine("Done!");

static int DoubleValue(int x)
{
    int result = x * 2;
    return result; // Step into here to inspect 'result'
}
