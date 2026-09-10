using System.Text;
using System.Text.RegularExpressions;

namespace MailLoadTester;

/// <summary>
/// Nahrazuje dynamické tagy v Subject/Body.
/// StringBuilder + podmíněné replace snižuje počet intermediate stringů na hot path.
/// </summary>
public static partial class TemplateTags
{
    private static readonly Regex RandomWordRegex = RandomWordPattern();

    public static string Process(string? input, int? testId = null)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";

        // Rychlá cesta: žádný tag
        if (input.IndexOf('{') < 0)
            return input;

        var sb = new StringBuilder(input.Length + 48);
        sb.Append(input);

        if (ContainsIgnoreCase(input, "{TIMESTAMP}"))
            sb.Replace("{TIMESTAMP}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        if (ContainsIgnoreCase(input, "{GUID}"))
            sb.Replace("{GUID}", Guid.NewGuid().ToString("D"));

        if (testId.HasValue && ContainsIgnoreCase(input, "{TEST_ID}"))
            sb.Replace("{TEST_ID}", testId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // {RANDOM_WORD:N} — regex na aktuálním obsahu
        if (ContainsIgnoreCase(input, "{RANDOM_WORD:"))
        {
            var s = RandomWordRegex.Replace(sb.ToString(), m =>
            {
                var len = 8;
                if (m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var n))
                    len = Math.Clamp(n, 1, 64);
                return RandomAlphanumeric(len);
            });
            sb.Clear();
            sb.Append(s);
        }

        if (ContainsIgnoreCase(input, "{RANDOM_WORD}"))
            sb.Replace("{RANDOM_WORD}", RandomAlphanumeric(8));

        return sb.ToString();
    }

    private static bool ContainsIgnoreCase(string hay, string needle) =>
        hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string RandomAlphanumeric(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        if (length <= 128)
        {
            Span<char> buf = stackalloc char[length];
            for (int i = 0; i < length; i++)
                buf[i] = chars[Random.Shared.Next(chars.Length)];
            return new string(buf);
        }
        var arr = new char[length];
        for (int i = 0; i < length; i++)
            arr[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(arr);
    }

    [GeneratedRegex(@"\{RANDOM_WORD:(\d+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RandomWordPattern();
}
