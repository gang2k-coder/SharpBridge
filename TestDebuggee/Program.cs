// Test debuggee for SharpBridge — a simple program to exercise debugging features

using System.Diagnostics;

// Check for --throw mode and --no-wait (skip ReadLine for launch tests)
bool throwMode = args.Length > 0 && args[0] == "--throw";
bool noWait = args.Length > 0 && args[0] == "--no-wait";
bool gapMode = args.Length > 0 && args[0] == "--gap";
bool spamMode = args.Contains("--spam");

// Show PID for attach and pause to give time to connect
Console.WriteLine($"PID: {Environment.ProcessId}");
if (!noWait)
{
    Console.WriteLine("Press ENTER to start...");
    Console.ReadLine();
}

Console.WriteLine("=== SharpBridge Test Debuggee ===");

// Some local variables to inspect
int counter = 0;
string message = "Hello from debuggee!";
var numbers = new List<int> { 10, 20, 30, 40, 50 };
// Extra variable forms for the variable-forms test (all visible at the
// counter++ breakpoint — declare BEFORE the loop, never reordered).
int[] arr = [1, 2, 3];
string? maybeNull = null;
ExceptionToThrow enumVar = ExceptionToThrow.Normal;
Person person = new("Ada", 36);
Dictionary<string, int> dict = new() { ["a"] = 1, ["b"] = 2 };
string multiLine = "line1\nline2\t\"quoted\"";
double ratio = 0.5;
decimal price = 19.99m;
char letter = 'A';
DateTime when = new(2026, 8, 3);

Console.WriteLine($"Starting loop with {numbers.Count} items...");

// Gap mode: sleep before the loop so a breakpoint hit falls into the
// "no tool call waiting" window (used by the stop-ledger tests).
if (gapMode)
{
    Console.WriteLine("Gap mode: sleeping 5s before the loop...");
    Thread.Sleep(5000);
}

// Loop — set breakpoints here to test continue/step
for (int i = 0; i < numbers.Count; i++)
{
    counter++;
    int current = numbers[i];
    int doubled = DoubleValue(current); // Step into this method
    int multiplied = Calculator.Multiply(current, 2);
    int added = Calculator.Add(current, 2);
    string greeting1 = Greeter.GetGreeting(message);
    string greeting2 = Greeter.GetGreeting("Gavin", "Liu");
    string processed = GenericProcessor<int>.Process(current);
    Console.WriteLine($"  [{i}] {current} * 2 = {doubled}, * 2 = {multiplied}, + 2 = {added}, counter = {counter}");

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
LoopEnd.Signal();

// --spam mode: burst of output AFTER the loop so breakpoints on loop lines
// keep their hardcoded line numbers. Exercises SharpBridge's output-log bounds.
if (spamMode)
{
    for (int i = 0; i < 8000; i++)
        Console.WriteLine($"spam line {i}");
}

static int DoubleValue(int x)
{
    int result = x * 2;
    return result; // Step into here to inspect 'result'
}

// === Function breakpoint test targets ===

class Calculator
{
    public static int Multiply(int x, int y)
    {
        return x * y;
    }

    public static int Add(int a, int b)
    {
        return a + b;
    }
}

class Greeter
{
    public static string GetGreeting(string name)
    {
        return $"Hello, {name}!";
    }

    // Overload — disambiguated by parameter count + types
    public static string GetGreeting(string firstName, string lastName)
    {
        return $"Hello, {firstName} {lastName}!";
    }
}

// For generic method + arity tests
class GenericProcessor<T>
{
    public static string Process(T item)
    {
        return $"Processed: {item}";
    }
}

// === Variable-form test targets (declared after Main — line numbers in
// Main stay stable; these only move if you edit this section) ===

enum ExceptionToThrow
{
    None,
    Normal
}

record Person(string Name, int Age);
