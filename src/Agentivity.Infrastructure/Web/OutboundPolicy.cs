using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Agentivity.Infrastructure.Web;

/// <summary>What the server calls out, for whom.</summary>
public enum OutboundCall
{
    /// <summary>
    /// An address someone wrote: a step's input, what an agent decided, a page that redirects, an image or a sound given by its
    /// address. Anyone may have written it: it stays on the public internet, in every edition, unless it is one the instance names
    /// as allowed.
    /// </summary>
    Address,

    /// <summary>
    /// A service the instance set up, in a credential or a setting: a model, a vector store, a search engine, a mail server. It
    /// may be on the internal network where the instance allows it (a model or a vector store on the same machine, in Community).
    /// </summary>
    Service,
}

/// <summary>
/// The one policy of what the server calls out: every HTTP client of the service connects through it (<see cref="Handler"/>), and
/// so does the mail (<see cref="ConnectAsync"/>). It is applied to the address actually connected to, which no redirect and no
/// name resolving inside can get around, and it is the one in force when connecting: changing it applies to the next
/// connection.
/// <list type="bullet">
/// <item>the public internet: always;</item>
/// <item>the internal network (the machine itself, a private network): for a service the instance set up where
/// <see cref="ServicesMayReachInternal"/>, and for the addresses named in <see cref="AllowedInternal"/>;</item>
/// <item>the link-local range, where clouds serve the credentials of the machine (169.254.169.254): never.</item>
/// </list>
/// </summary>
public sealed class OutboundPolicy
{
    /// <summary>The policy in force: Community's (services may reach the internal network) until the instance says otherwise.</summary>
    public static OutboundPolicy Current { get; private set; } = new(servicesMayReachInternal: true, allowedInternal: []);

    /// <summary>Puts <paramref name="policy"/> in force, for the next connections.</summary>
    public static void Use(OutboundPolicy policy) => Current = policy ?? throw new ArgumentNullException(nameof(policy));

    // A refusal is told once per address in this while: a step retrying does not fill the journal.
    private static readonly TimeSpan TellEvery = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _told = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="servicesMayReachInternal">Whether a service the instance set up may be on the internal network.</param>
    /// <param name="allowedInternal">The internal addresses reached all the same: a name (<c>qdrant.internal</c>), a name and a port
    /// (<c>qdrant.internal:6333</c>), an address, or a range (<c>192.168.1.0/24</c>).</param>
    public OutboundPolicy(bool servicesMayReachInternal, IReadOnlyList<string> allowedInternal)
    {
        ServicesMayReachInternal = servicesMayReachInternal;
        AllowedInternal = [.. allowedInternal.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public bool ServicesMayReachInternal { get; }

    public IReadOnlyList<string> AllowedInternal { get; }

    /// <summary>Told of a refusal (the host, why), at most once per address every few minutes: for the journal.</summary>
    public Action<string, string>? Refused { get; init; }

    /// <summary>Why <paramref name="host"/> (resolved to <paramref name="addresses"/>) is not called, or null when it is.</summary>
    public string? Refusal(string host, int port, IReadOnlyList<IPAddress> addresses, OutboundCall call)
    {
        string? why = null;
        if (addresses.Count == 0)
            why = $"{host} does not resolve to any address.";
        else if (addresses.Any(IsLinkLocal))
            why = $"{PublicNetwork.Refusal(host)} The link-local range is never called, even named as allowed: it is where a cloud gives the credentials of its machines.";
        else if (addresses.Any(PublicNetwork.IsInternal) && !(call == OutboundCall.Service && ServicesMayReachInternal) && !IsAllowed(host, port, addresses))
            why = PublicNetwork.Refusal(host);

        if (why is not null && Refused is { } tell)
        {
            var now = DateTimeOffset.UtcNow;
            var key = $"{host}:{port}";
            if (!_told.TryGetValue(key, out var last) || now - last >= TellEvery)
            {
                _told[key] = now;
                tell(host, why);
            }
        }

        return why;
    }

    /// <summary>
    /// Why <paramref name="entry"/> cannot be an internal address allowed, or null when it can: a name (with a port or not), an
    /// address, or a range — never the link-local range.
    /// </summary>
    public static string? EntryProblem(string entry)
    {
        entry = entry.Trim();
        if (entry.Contains('/'))
        {
            if (!Range.TryParse(entry, out var range))
                return $"'{entry}' is not a range of addresses: write it as 192.168.1.0/24.";
            return range.Contains(IPAddress.Parse("169.254.169.254")) || range.Contains(IPAddress.Parse("fe80::1")) ? $"'{entry}' holds the link-local range, which is never called." : null;
        }

        var (name, _) = SplitPort(entry);
        if (IPAddress.TryParse(name, out var address))
            return IsLinkLocal(address) ? $"'{entry}' is in the link-local range, which is never called." : null;
        return Uri.CheckHostName(name) == UriHostNameType.Dns ? null : $"'{entry}' is not an address: write a name (qdrant.internal), a name and a port (qdrant.internal:6333), an address or a range.";
    }

    // Named as allowed: the name (with its port when one is given), the address, or the range it is in.
    private bool IsAllowed(string host, int port, IReadOnlyList<IPAddress> addresses)
    {
        foreach (var entry in AllowedInternal)
        {
            if (entry.Contains('/'))
            {
                if (Range.TryParse(entry, out var range) && addresses.All(range.Contains))
                    return true;
                continue;
            }

            var (name, entryPort) = SplitPort(entry);
            if (entryPort is not null && entryPort != port)
                continue;
            if (string.Equals(name, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                return true;
            if (IPAddress.TryParse(name, out var literal) && addresses.All(a => Normal(a).Equals(Normal(literal))))
                return true;
        }

        return false;
    }

    private static (string Name, int? Port) SplitPort(string entry)
    {
        // An IPv6 address with a port is written [fd00::1]:8080; without, as is.
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']');
            return close > 0 && close + 2 < entry.Length && entry[close + 1] == ':' && int.TryParse(entry[(close + 2)..], out var p6) ? (entry[1..close], p6) : (entry.Trim('[', ']'), null);
        }

        var colon = entry.LastIndexOf(':');
        return colon > 0 && entry.IndexOf(':') == colon && int.TryParse(entry[(colon + 1)..], out var p) ? (entry[..colon], p) : (entry, null);
    }

    private static IPAddress Normal(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsLinkLocal(IPAddress address)
    {
        address = Normal(address);
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[0] == 169 && bytes[1] == 254 : address.IsIPv6LinkLocal;
    }

    /// <summary>
    /// The handler every HTTP client of the server connects through, for <paramref name="call"/>: it resolves the host itself and
    /// connects only where the policy in force allows. It follows redirects on its own only when <paramref name="followRedirects"/>
    /// (each address it is sent to is then checked as it connects).
    /// </summary>
    public static SocketsHttpHandler Handler(OutboundCall call, bool followRedirects = false, TimeSpan? pooledConnectionLifetime = null) =>
        new()
        {
            AllowAutoRedirect = followRedirects,
            PooledConnectionLifetime = pooledConnectionLifetime ?? TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, cancellationToken) => new NetworkStream(await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, call, cancellationToken).ConfigureAwait(false), ownsSocket: true),
        };

    /// <summary>A socket connected to <paramref name="host"/>, where the policy in force allows it (the mail connects through it).</summary>
    public static async Task<Socket> ConnectAsync(string host, int port, OutboundCall call, CancellationToken cancellationToken)
    {
        var bare = host.Trim('[', ']');
        IPAddress[] addresses = IPAddress.TryParse(bare, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(bare, cancellationToken).ConfigureAwait(false);
        if (Current.Refusal(bare, port, addresses, call) is { } why)
            throw new PrivateNetworkRefusedException(why);

        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A range of addresses, as 192.168.1.0/24 or fd00::/8.
    private readonly record struct Range(byte[] Network, int Bits)
    {
        public static bool TryParse(string text, out Range range)
        {
            range = default;
            var parts = text.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var bits))
                return false;
            var bytes = Normal(network).GetAddressBytes();
            if (bits < 0 || bits > bytes.Length * 8)
                return false;
            range = new Range(bytes, bits);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            var bytes = Normal(address).GetAddressBytes();
            if (bytes.Length != Network.Length)
                return false;
            for (var bit = 0; bit < Bits; bit++)
            {
                var mask = (byte)(0x80 >> (bit % 8));
                if ((bytes[bit / 8] & mask) != (Network[bit / 8] & mask))
                    return false;
            }

            return true;
        }
    }
}
