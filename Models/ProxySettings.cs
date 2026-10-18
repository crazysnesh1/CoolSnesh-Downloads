using System.Text.Json.Serialization;

namespace CoolSnesh.Avalonia.Models;

public sealed class ProxySettings
{
    public bool Enabled { get; set; }
    public string Type { get; set; } = "HTTP";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";

    [JsonIgnore]
    public string Password { get; set; } = "";

    public ProxySettings Clone() => new()
    {
        Enabled = Enabled, Type = Type, Host = Host, Port = Port,
        Username = Username, EncryptedPassword = EncryptedPassword, Password = Password
    };
}
