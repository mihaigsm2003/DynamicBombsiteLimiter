using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicBombsiteLimiter;

public static partial class MessageFormatter
{
    public static string BuildChat(string prefix, string message) =>
        string.IsNullOrWhiteSpace(prefix) ? $" {message}" : $" {prefix} {message}";

    public static string StripForConsole(string text) =>
        ConsoleNoise().Replace(text, string.Empty);

    public static bool TryFormat(string template, object[] args, out string result)
    {
        try
        {
            result = string.Format(CultureInfo.InvariantCulture, template, args);
            return true;
        }
        catch (FormatException)
        {
            result = template;
            return false;
        }
    }

    [GeneratedRegex(@"\{[a-zA-Z]+\}|[\x00-\x1F]")]
    private static partial Regex ConsoleNoise();
}
