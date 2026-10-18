using System.Globalization;

namespace CoolSnesh.Avalonia.Services;

public sealed record ProxyEndpointParts(string Host, int Port, string Username, string Password, string? Type = null);

/// <summary>Parses common host:port:login:password proxy lists pasted as one string.</summary>
public static class ProxyEndpointParser
{
    public static bool TryParse(string? value, out ProxyEndpointParts parts)
    {
        parts = new ProxyEndpointParts("", 0, "", "");
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        string? type = null;
        var schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator > 0)
        {
            var scheme = text[..schemeSeparator].Trim().ToLowerInvariant();
            type = scheme switch
            {
                "http" => "HTTP",
                "https" => "HTTPS",
                "socks4" => "SOCKS4",
                "socks5" => "SOCKS5",
                _ => null
            };
            if (type is null) return false;
            text = text[(schemeSeparator + 3)..];
        }

        // Split at most four parts so colons inside a password are preserved.
        var segments = text.Split(':', 4, StringSplitOptions.None);
        if (segments.Length != 4) return false;

        var host = segments[0].Trim();
        var portText = segments[1].Trim();
        var username = segments[2].Trim();
        var password = segments[3];
        if (string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
            return false;

        parts = new ProxyEndpointParts(host, port, username, password, type);
        return true;
    }
}
