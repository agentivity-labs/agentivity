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

    /// <summary>The rule every web call of the platform follows (see <see cref="Agentivity.Infrastructure.Web.PublicNetwork"/>).</summary>
    public static bool IsInternal(IPAddress address) => Agentivity.Infrastructure.Web.PublicNetwork.IsInternal(address);
}
