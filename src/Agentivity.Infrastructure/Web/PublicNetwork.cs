using System.Net;
using System.Net.Sockets;

namespace Agentivity.Infrastructure.Web;

/// <summary>
/// What a workflow step or an agent calls on the web stays on the public internet: never the local machine, a private
/// network, or the link-local range cloud providers serve instance credentials from. It holds in every edition — whoever
/// writes the address (a person, an agent, a page that redirects), the server must not become a way into the network it
/// runs in. The rule is applied twice: before a call (<see cref="Precheck"/>, for a clear refusal) and on the address
/// actually connected to (<see cref="CreateHandler"/>), which no redirect and no name resolving inside can get around.
/// </summary>
public static class PublicNetwork
{
    /// <summary>The named HTTP client that only reaches public addresses and follows no redirect (the caller checks each one).</summary>
    public const string HttpClientName = "agentivity-public-web";

    /// <summary>
    /// The same, following redirects on its own — for the scrapers, which read pages that often redirect (http to https, a
    /// missing www). Each address it is sent to is still checked when it connects.
    /// </summary>
    public const string FollowingRedirectsHttpClientName = "agentivity-public-web-redirects";

    /// <summary>Why a call to <paramref name="host"/> is refused.</summary>
    public static string Refusal(string host) => $"Calls to the local or private network are not allowed ({host}).";

    /// <summary>
    /// Null when <paramref name="uri"/> may be called as far as can be told without resolving its host, otherwise why not: a
    /// web address, not an internal address written as is, not the local machine by name. What a name resolves to is checked
    /// when connecting (see <see cref="CreateHandler"/>); this check only says the obvious cases clearly, before anything is sent.
    /// </summary>
    public static string? Precheck(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            return $"'{uri}' is not a web address (http or https).";
        }

        string host = uri.Host.Trim('[', ']');
        bool localByName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        return localByName || (IPAddress.TryParse(host, out IPAddress? literal) && IsInternal(literal)) ? Refusal(uri.Host) : null;
    }

    /// <summary>Whether <paramref name="exception"/> (or what it wraps) is a call refused for leading inside the network.</summary>
    public static bool IsRefusal(Exception? exception) => RefusalOf(exception) is not null;

    /// <summary>Why the call was refused, when <paramref name="exception"/> (or what it wraps) is such a refusal; null otherwise.</summary>
    public static string? RefusalOf(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PrivateNetworkRefusedException refused)
                return refused.Message;
        }

        return null;
    }

    /// <summary>
    /// The handler of the HTTP clients for public calls: it resolves the host itself and refuses to connect to an internal
    /// address — for the first address as for every one a redirect leads to. It follows redirects on its own only when
    /// <paramref name="followRedirects"/> (each new address is then checked as it connects).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool followRedirects = false) =>
        new()
        {
            AllowAutoRedirect = followRedirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                IPAddress? target = addresses.FirstOrDefault();
                if (target is null || addresses.Any(IsInternal))
                {
                    throw new PrivateNetworkRefusedException(Refusal(host));
                }

                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

    /// <summary>Whether <paramref name="address"/> is the local machine, a private or link-local network, or not routable.</summary>
    public static bool IsInternal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168) // 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254) // link-local, incl. cloud metadata 169.254.169.254
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) // carrier-grade NAT 100.64.0.0/10
                || bytes[0] == 0
                || bytes[0] >= 224; // multicast and reserved
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC); // fc00::/7 unique local
    }
}

/// <summary>A connection refused because the address leads to the local machine or a private network (see <see cref="PublicNetwork"/>).</summary>
public sealed class PrivateNetworkRefusedException(string message) : HttpRequestException(message);
