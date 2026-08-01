using System;

Console.WriteLine("Anything to say?");

while (true)
{
    var input = Console.ReadLine();

    if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(input, "bye", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine();
        Console.WriteLine("Ok, Bye!");
        break;
    }

    Console.WriteLine();
    Console.WriteLine($"You said: {input}, anything else?");
}
