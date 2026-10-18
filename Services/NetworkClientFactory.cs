using System.Net;
using System.Net.Sockets;
using System.Text;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

/// <summary>Creates the shared network stack used by release checks, catalog requests and downloads.</summary>
public static class NetworkClientFactory
{
    public static HttpClient Create(ProxySettings? settings, TimeSpan? timeout = null)
    {
        var proxy = settings?.Clone() ?? new ProxySettings();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        if (proxy.Enabled)
        {
            Validate(proxy);
            proxy.Type = proxy.Type.Trim().ToUpperInvariant();
            switch (proxy.Type)
            {
                case "HTTP":
                case "HTTPS":
                    var uri = new UriBuilder(proxy.Type.Equals("HTTPS", StringComparison.OrdinalIgnoreCase) ? "https" : "http", proxy.Host, proxy.Port).Uri;
                    var webProxy = new WebProxy(uri);
                    if (!string.IsNullOrWhiteSpace(proxy.Username))
                        webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                    handler.Proxy = webProxy;
                    handler.UseProxy = true;
                    break;
                case "SOCKS4":
                case "SOCKS5":
                    handler.UseProxy = false;
                    handler.ConnectCallback = (context, token) => ConnectThroughSocksAsync(context, proxy, token);
                    break;
                default:
                    handler.Dispose();
                    throw new InvalidOperationException("Неизвестный тип прокси.");
            }
        }

        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CoolSneshDownloads/1.1");
        return client;
    }

    public static void Validate(ProxySettings settings)
    {
        if (!settings.Enabled) return;
        if (string.IsNullOrWhiteSpace(settings.Host)) throw new InvalidOperationException("Укажите хост прокси.");
        if (settings.Port is < 1 or > 65535) throw new InvalidOperationException("Порт должен быть от 1 до 65535.");
        var type = settings.Type?.Trim().ToUpperInvariant();
        if (type is not ("HTTP" or "HTTPS" or "SOCKS4" or "SOCKS5"))
            throw new InvalidOperationException("Выберите тип HTTP, HTTPS, SOCKS4 или SOCKS5.");
    }

    private static async ValueTask<Stream> ConnectThroughSocksAsync(SocketsHttpConnectionContext context, ProxySettings proxy, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(proxy.Host, proxy.Port), cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);
            if (proxy.Type.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase))
                await Socks5ConnectAsync(stream, context.DnsEndPoint.Host, context.DnsEndPoint.Port, proxy, cancellationToken);
            else
                await Socks4ConnectAsync(stream, context.DnsEndPoint.Host, context.DnsEndPoint.Port, proxy.Username, cancellationToken);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task Socks5ConnectAsync(Stream stream, string host, int port, ProxySettings proxy, CancellationToken token)
    {
        var user = Encoding.UTF8.GetBytes(proxy.Username ?? "");
        var pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
        var wantsAuth = user.Length > 0;
        if (user.Length > 255 || pass.Length > 255) throw new InvalidOperationException("Логин и пароль SOCKS5 должны занимать не более 255 байт.");

        var hello = wantsAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 };
        await stream.WriteAsync(hello, token);
        var selected = new byte[2];
        await ReadExactAsync(stream, selected, token);
        if (selected[0] != 5 || selected[1] == 0xFF) throw new IOException("SOCKS5 прокси не принял доступные методы авторизации.");
        if (selected[1] == 2)
        {
            if (!wantsAuth) throw new IOException("SOCKS5 требует логин и пароль.");
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1; auth[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, auth, 2, user.Length);
            auth[2 + user.Length] = (byte)pass.Length;
            Buffer.BlockCopy(pass, 0, auth, 3 + user.Length, pass.Length);
            await stream.WriteAsync(auth, token);
            var authResult = new byte[2];
            await ReadExactAsync(stream, authResult, token);
            if (authResult[1] != 0) throw new IOException("SOCKS5 отклонил логин или пароль.");
        }
        else if (selected[1] != 0) throw new IOException($"SOCKS5 выбрал неподдерживаемый метод авторизации {selected[1]}.");

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > 255) throw new IOException("Имя узла слишком длинное для SOCKS5.");
        var request = new byte[7 + hostBytes.Length];
        request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[^2] = (byte)(port >> 8); request[^1] = (byte)(port & 0xFF);
        await stream.WriteAsync(request, token);

        var reply = new byte[4];
        await ReadExactAsync(stream, reply, token);
        if (reply[0] != 5 || reply[1] != 0) throw new IOException($"SOCKS5 не смог подключиться к узлу (код {reply[1]}).");
        var addressLength = reply[3] switch { 1 => 4, 4 => 16, 3 => await ReadByteLengthAsync(stream, token), _ => throw new IOException("Некорректный ответ SOCKS5.") };
        var remaining = new byte[addressLength + 2];
        await ReadExactAsync(stream, remaining, token);
    }

    private static async Task Socks4ConnectAsync(Stream stream, string host, int port, string username, CancellationToken token)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var userBytes = Encoding.UTF8.GetBytes(username ?? "");
        var isIp = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        if (userBytes.Length > 255) throw new InvalidOperationException("Логин SOCKS4 должен занимать не более 255 байт.");
        var isSocks4a = !isIp;
        var bytes = new List<byte> { 4, 1, (byte)(port >> 8), (byte)(port & 0xFF) };
        if (isIp) bytes.AddRange(ip!.GetAddressBytes());
        else bytes.AddRange(new byte[] { 0, 0, 0, 1 });
        bytes.AddRange(userBytes); bytes.Add(0);
        if (isSocks4a) { bytes.AddRange(hostBytes); bytes.Add(0); }
        await stream.WriteAsync(bytes.ToArray(), token);
        var reply = new byte[8];
        await ReadExactAsync(stream, reply, token);
        if (reply[1] != 0x5A) throw new IOException($"SOCKS4 прокси отклонил соединение (код {reply[1]}). SOCKS4 поддерживает user-id, но не пароль.");
    }

    private static async Task<byte> ReadByteLengthAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1]; await ReadExactAsync(stream, one, token); return one[0];
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0) throw new EndOfStreamException("Прокси закрыл соединение во время рукопожатия.");
            offset += read;
        }
    }
}
