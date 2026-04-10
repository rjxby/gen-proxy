namespace GenProxy.StackRunner;

internal static class ConsoleStyling
{
    private const string Reset = "\u001b[0m";
    private const string Blue = "\u001b[34m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";

    private static readonly bool ColorEnabled =
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR")) &&
        !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase) &&
        !Console.IsOutputRedirected;

    public static void Info(string message) => WriteLine(Console.Out, message, Blue);

    public static void Success(string message) => WriteLine(Console.Out, message, Green);

    public static void Warning(string message) => WriteLine(Console.Out, message, Yellow);

    public static void Error(string message) => WriteLine(Console.Error, message, Red);

    private static void WriteLine(TextWriter writer, string message, string color)
    {
        if (ColorEnabled)
        {
            writer.WriteLine($"{color}{message}{Reset}");
            return;
        }

        writer.WriteLine(message);
    }
}
