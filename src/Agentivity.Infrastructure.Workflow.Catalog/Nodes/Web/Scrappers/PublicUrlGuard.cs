using System.Net;
using System.Net.Sockets;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Refuses URLs that point inside the network the backend runs in (loopback, private ranges, the
/// link-local range cloud providers serve instance credentials from). Needed wherever a URL typed by
/// a person is fetched server-side — without it, "paste a link to my CV" is also "make the server call
/// anything it can reach". Best effort: it checks where the host name resolves before the request; it
/// cannot see a redirect the scraper follows afterwards.
/// </summary>
public static class PublicUrlGuard
{
    /// <returns>Null when the URL is allowed, otherwise a message saying why not.</returns>
    public static async ValueTask<string?> CheckAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return $"'{url}' is not a valid http(s) URL.";
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return $"The host '{uri.Host}' could not be found.";
            }
        }

        return addresses.Length == 0 || addresses.Any(IsInternal) ? $"'{uri.Host}' points to a private or internal address, which is not allowed." : null;
    }

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
