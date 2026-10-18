using System.Net;
using System.Text.RegularExpressions;

namespace CoolSnesh.Avalonia.Services;

/// <summary>Resolves a Google Drive share URL to a direct file download URL, including the large-file confirmation form.</summary>
public static class GoogleDriveDownloadResolver
{
    public static bool TryExtractFileId(string? sharedUrl, out string fileId)
    {
        fileId = "";
        if (!Uri.TryCreate(sharedUrl, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)) return false;

        var pathMatch = Regex.Match(uri.AbsolutePath, @"/file/d/(?<id>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant);
        if (pathMatch.Success)
        {
            fileId = pathMatch.Groups["id"].Value;
            return true;
        }

        var query = QueryStringParser.Parse(uri.Query);
        if (query.TryGetValue("id", out var id) && Regex.IsMatch(id, @"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
        {
            fileId = id;
            return true;
        }
        return false;
    }

    public static async Task<string> ResolveDirectDownloadUrlAsync(HttpClient client, string sharedUrl, CancellationToken cancellationToken = default)
    {
        if (!TryExtractFileId(sharedUrl, out var fileId))
            throw new InvalidOperationException("Ссылка Google Drive не содержит корректный ID файла.");

        var initialUrl = $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}";
        using var response = await client.GetAsync(initialUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            return initialUrl;

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(cancellationToken));
        var form = Regex.Match(html,
            """<form\b(?=[^>]*(?:id=['"]download-form['"]|action=['"][^'"]*(?:uc|download)))[^>]*>(?<body>.*?)</form>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (form.Success)
        {
            var formTag = Regex.Match(form.Value, @"^<form\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var actionMatch = Regex.Match(formTag.Groups["attrs"].Value,
                """action\s*=\s*['"](?<url>[^'"]+)['"]""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (actionMatch.Success)
            {
                var actionText = WebUtility.HtmlDecode(actionMatch.Groups["url"].Value);
                var action = Uri.TryCreate(actionText, UriKind.Absolute, out var absolute)
                    ? absolute
                    : new Uri(response.RequestMessage?.RequestUri ?? new Uri(initialUrl), actionText);
                if (IsGoogleDownloadHost(action.Host))
                {
                    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Match input in Regex.Matches(form.Groups["body"].Value,
                                 """<input\b(?=[^>]*type=['"]hidden['"])[^>]*>""",
                                 RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
                    {
                        var name = GetAttribute(input.Value, "name");
                        var value = GetAttribute(input.Value, "value");
                        if (!string.IsNullOrWhiteSpace(name)) parameters[name] = value ?? "";
                    }
                    parameters.TryAdd("id", fileId);
                    parameters.TryAdd("export", "download");
                    return AddQuery(action, parameters).ToString();
                }
            }
        }

        // A simple confirmation token is also used by some Drive download interstitials.
        var confirm = Regex.Match(html,
            """(?:name=['"]confirm['"]\s+value=['"](?<token>[^'"]+)['"]|[?&]confirm=(?<token2>[^&'"<>\s]+))""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var token = confirm.Groups["token"].Success ? confirm.Groups["token"].Value : confirm.Groups["token2"].Value;
        if (!string.IsNullOrWhiteSpace(token))
            return $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}&confirm={Uri.EscapeDataString(token)}";

        throw new InvalidOperationException("Google Drive не вернул прямую ссылку на файл. Проверь, что доступ к файлу разрешён всем, у кого есть ссылка, и повтори попытку.");
    }

    private static bool IsGoogleDownloadHost(string host) =>
        host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase);

    private static Uri AddQuery(Uri uri, IReadOnlyDictionary<string, string> parameters)
    {
        var builder = new UriBuilder(uri);
        var merged = QueryStringParser.Parse(builder.Query);
        foreach (var pair in parameters) merged[pair.Key] = pair.Value;
        builder.Query = string.Join("&", merged.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri;
    }

    private static string? GetAttribute(string tag, string attribute)
    {
        var match = Regex.Match(tag,
            $"""\b{Regex.Escape(attribute)}\s*=\s*['"](?<value>[^'"]*)['"]""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }
}

/// <summary>Small query parser to avoid a System.Web dependency in the desktop project.</summary>
internal static class QueryStringParser
{
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = separator < 0 ? segment : segment[..separator];
            var value = separator < 0 ? "" : segment[(separator + 1)..];
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }
}
