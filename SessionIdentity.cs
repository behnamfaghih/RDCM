namespace RDCM;

public sealed record SessionIdentity(
    Guid SessionId,
    Guid ProfileId,
    string Name,
    string Host,
    int Port,
    string Username,
    string Group)
{
    public string Address
    {
        get
        {
            var host = Host.Trim();
            var unwrappedHost = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
            if (System.Net.IPAddress.TryParse(unwrappedHost, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                host = $"[{unwrappedHost}]";
            }

            return Port == 3389 ? host : $"{host}:{Port}";
        }
    }

    public static SessionIdentity Capture(ConnectionProfile profile) =>
        new(Guid.NewGuid(), profile.Id, profile.Name, profile.Host, profile.Port, profile.Username, profile.Group);
}
