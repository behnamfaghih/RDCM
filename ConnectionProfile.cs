using System.Net;
using System.Net.Sockets;

namespace RDCM;

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Group { get; set; } = "General";
    public string Notes { get; set; } = "";

    public string Address
    {
        get
        {
            var host = Host.Trim();
            var unwrappedHost = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
            if (IPAddress.TryParse(unwrappedHost, out var address) &&
                address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = $"[{unwrappedHost}]";
            }

            return Port == 3389 ? host : $"{host}:{Port}";
        }
    }

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            host.Any(char.IsWhiteSpace) || ContainsLineBreak(host))
        {
            return false;
        }

        var address = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(address, out _))
        {
            return true;
        }

        return Uri.CheckHostName(host) == UriHostNameType.Dns &&
               host.Split('.').All(label =>
                   label.Length is > 0 and <= 63 &&
                   label[0] != '-' &&
                   label[^1] != '-' &&
                   label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    public static bool ContainsLineBreak(string? value) =>
        value?.Contains('\r') == true || value?.Contains('\n') == true;
}
