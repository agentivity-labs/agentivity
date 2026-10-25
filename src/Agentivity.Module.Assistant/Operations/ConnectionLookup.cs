using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>An account or connection the person already has (a model provider, a mailbox, a site…). Never its secrets.</summary>
public sealed record ConnectionRef(string Id, string Name, string Kind, string? Description);

/// <summary>What the assistant may know about the person's connections: names and kinds, so it can use one instead of asking.</summary>
public interface IConnectionLookup
{
    Task<IReadOnlyList<ConnectionRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The kinds of connection the platform knows how to create, by their kind and the name people know them by. It is what
    /// lets the person be offered to create the connection that is missing, instead of being sent away to do it.
    /// </summary>
    IReadOnlyList<ConnectionKindRef> ListKinds() => [];
}

/// <summary>A kind of connection that can be created: its id (the template) and its name.</summary>
public sealed record ConnectionKindRef(string Id, string Name);

public sealed class CredentialsConnectionLookup(CredentialsService credentials) : IConnectionLookup
{
    public async Task<IReadOnlyList<ConnectionRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        var all = await credentials.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. all
                .Where(c => kind is null || string.Equals(c.TemplateId, kind, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ConnectionRef(c.Id, c.Name, c.TemplateId, c.Description)),
        ];
    }

    public IReadOnlyList<ConnectionKindRef> ListKinds() => [.. CredentialsTemplateRegistry.GetAll().Select(t => new ConnectionKindRef(t.Id, t.DisplayName))];
}
