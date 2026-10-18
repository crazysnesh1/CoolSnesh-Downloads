using System.Text.RegularExpressions;

namespace CoolSnesh.Avalonia.Services;

/// <summary>
/// Normalizes and compares version strings from different publishers.
/// Numeric components are compared as if missing trailing components were zero.
/// </summary>
public static class VersionComparison
{
    private static readonly Regex BuildSuffix = new(@"\s*\([^()]*\)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Prefix = new(@"^(?:release[\s_-]*|version\s+|ver\.?\s*|v\s*)(?=\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumericVersion = new(@"^(?<core>\d+(?:\.\d+)*)(?:[-+].*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Returns only a parseable numeric version core, or null if the input is empty/invalid.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = BuildSuffix.Replace(value.Trim(), string.Empty).Trim();
        normalized = Prefix.Replace(normalized, string.Empty).Trim();

        var match = NumericVersion.Match(normalized);
        if (!match.Success) return null;

        var core = match.Groups["core"].Value;
        var parts = core.Split('.');
        var parsed = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var component))
                return null;
            parsed.Add(component);
        }

        while (parsed.Count > 1 && parsed[^1] == 0) parsed.RemoveAt(parsed.Count - 1);
        return string.Join(".", parsed);
    }

    /// <summary>Compares two versions; returns null when either value is not a valid numeric version.</summary>
    public static int? Compare(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft is null || normalizedRight is null) return null;

        var leftParts = normalizedLeft.Split('.').Select(long.Parse).ToArray();
        var rightParts = normalizedRight.Split('.').Select(long.Parse).ToArray();
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var leftPart = i < leftParts.Length ? leftParts[i] : 0;
            var rightPart = i < rightParts.Length ? rightParts[i] : 0;
            var result = leftPart.CompareTo(rightPart);
            if (result != 0) return result;
        }
        return 0;
    }

    public static bool IsAvailableLatestVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not ("Ошибка проверки" or "Не определяется источником" or "Версия не указана" or "Не проверена") &&
        Normalize(value) is not null;
}
