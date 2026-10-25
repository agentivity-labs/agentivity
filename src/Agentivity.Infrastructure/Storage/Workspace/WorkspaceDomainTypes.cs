using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The object types a domain of the workspace holds, and how to tell them apart in what was kept without them: the Json.Advanced
/// module kept only the content of an object, never its type, and a workspace that takes it over must give each object its type.
/// <para>
/// The type of an object kept without one is, in this order: what <see cref="TypeOf"/> says (given the id of the object and its
/// content); the <c>kind</c> of its content, when it is one of <see cref="ObjectTypes"/>; the type of the domain, when it holds
/// only one. Null when none of them tells.
/// </para>
/// </summary>
/// <param name="SpaceScoped">
/// Each object of the domain belongs to a space (the libraries): its store reads and writes only in the spaces of the work being
/// done now. The domains of the instance itself (its people, its settings, its journal) are not, nor what it shares among the
/// spaces (the credentials, the configurations: see <see cref="Identity.SpaceAvailability"/>).
/// </param>
public sealed record WorkspaceDomainTypes(IReadOnlyList<string> ObjectTypes, Func<string, JsonElement, string?>? TypeOf = null, bool SpaceScoped = false)
{
    public string? TypeOfKept(string objectId, JsonElement content)
    {
        if (TypeOf?.Invoke(objectId, content) is { Length: > 0 } told && ObjectTypes.Contains(told, StringComparer.OrdinalIgnoreCase))
            return ObjectTypes.First(t => string.Equals(t, told, StringComparison.OrdinalIgnoreCase));

        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            && ObjectTypes.FirstOrDefault(t => string.Equals(t, kind.GetString(), StringComparison.OrdinalIgnoreCase)) is { } ofKind)
            return ofKind;

        return ObjectTypes.Count == 1 ? ObjectTypes[0] : null;
    }
}
