// Long-loop repro debuggee: the loop effectively never finishes, so capture
// breakpoints on lines 11 and 12 keep firing as the debugger ping-pongs the
// program between the two lines on every continue.

Console.WriteLine($"PID: {Environment.ProcessId}");
Console.WriteLine("Press ENTER to start...");
Console.ReadLine();

int counter = 0;
for (long i = 0; i < 10_000_000_000_000; i++)
{
    counter++;
    if (i % 1_000_000 == 0)
        Console.WriteLine($"i={i} counter={counter}");
}

Console.WriteLine($"Done: {counter}");
