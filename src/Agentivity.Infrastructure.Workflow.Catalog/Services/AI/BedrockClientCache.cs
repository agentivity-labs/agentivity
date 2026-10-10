using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

/// <summary>
/// The AWS Bedrock clients the application has built, one per credentials id. A client owns a real
/// <c>AmazonBedrockRuntimeClient</c> (disposable, its own connection pool), so it is reused rather than
/// rebuilt on every call — the same reason an <c>HttpClient</c> is. It lives here, in one service for the
/// whole application, and not in the Bedrock model node: a node is not what survives between two calls, so
/// a cache kept inside one would be thrown away with it and every call would open a new connection pool.
/// <para>
/// A client is only valid for the credentials it was built from, so each entry remembers a fingerprint of them:
/// when the credentials behind an id are edited, the next call builds a new client, and the old one — which calls
/// already under way may still be using — is disposed after a grace period rather than cut from under them.
/// The container disposes the rest when the application shuts down.
/// </para>
/// </summary>
public sealed class BedrockClientCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _retireAfter;

    /// <param name="retireAfter">How long a replaced client is kept for calls still using it. Default 5 minutes.</param>
    public BedrockClientCache(TimeSpan? retireAfter = null) => _retireAfter = retireAfter ?? TimeSpan.FromMinutes(5);

    /// <param name="fingerprint">Identifies the credentials the client is built from (any change must change it).</param>
    /// <param name="create">Builds a client; called when this id has none yet, or only one built from other credentials.</param>
    public BedrockLlmClient GetOrCreate(string credentialsId, string fingerprint, Func<BedrockLlmClient> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(create);

        lock (_gate)
        {
            if (_entries.TryGetValue(credentialsId, out Entry? existing))
            {
                if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return existing.Client;
                }

                Retire(existing.Client);
            }

            BedrockLlmClient client = create();
            _entries[credentialsId] = new Entry(fingerprint, client);
            return client;
        }
    }

    private void Retire(BedrockLlmClient client) =>
        _ = Task.Delay(_retireAfter).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Client.Dispose();
            }

            _entries.Clear();
        }
    }

    private sealed record Entry(string Fingerprint, BedrockLlmClient Client);
}
