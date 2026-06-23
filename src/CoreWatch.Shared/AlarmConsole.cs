namespace CoreWatch.Shared;

public static class AlarmConsole
{
    private const string Reset = "\x1b[0m";
    private const string Yellow = "\x1b[93m";
    private const string Orange = "\x1b[38;2;255;140;0m";
    private const string Red = "\x1b[91m";

    public static void WriteLine(int priority, string message)
    {
        if (Console.IsOutputRedirected || priority is < 1 or > 3)
        {
            Console.WriteLine(message);
            return;
        }

        string color = priority switch
        {
            1 => Yellow,
            2 => Orange,
            3 => Red,
            _ => string.Empty
        };

        Console.WriteLine($"{color}{message}{Reset}");
    }
}
