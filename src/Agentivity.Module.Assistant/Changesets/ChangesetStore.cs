using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Module.Assistant.Changesets;

/// <summary>Keeps changesets between requests. A changeset is personal work in progress: nothing else reads it.</summary>
public interface IChangesetStore
{
    Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default);

    Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class InMemoryChangesetStore : IChangesetStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Changeset>>([.. _items.Values.Select(Read).OrderByDescending(c => c.UpdatedAt)]);

    public Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryGetValue(id, out var json) ? Read(json) : null);

    public Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default)
    {
        changeset.UpdatedAt = DateTimeOffset.UtcNow;
        _items[changeset.Id] = ChangesetJson.Write(changeset);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryRemove(id, out _));

    private static Changeset Read(string json) => ChangesetJson.Read(json);
}

/// <summary>One JSON file per changeset under the data folder. Written through a temporary file so a crash never leaves half a changeset.</summary>
public sealed class FileChangesetStore : IChangesetStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileChangesetStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async Task<IReadOnlyList<Changeset>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
            return [];

        var list = new List<Changeset>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                list.Add(ChangesetJson.Read(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)));
            }
            catch (JsonException)
            {
                // A file that is not a changeset is left alone; it must not hide the others.
            }
        }

        return [.. list.OrderByDescending(c => c.UpdatedAt)];
    }

    public async Task<Changeset?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
            return null;
        return ChangesetJson.Read(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    public async Task SaveAsync(Changeset changeset, CancellationToken cancellationToken = default)
    {
        changeset.UpdatedAt = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathOf(changeset.Id);
            var temporary = path + ".tmp";
            var json = ChangesetJson.Write(changeset);

            // On Windows an antivirus or an indexer can hold the file for a moment right after it was written or deleted: the
            // write is tried again a few times before it is given up on.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    await Task.Delay(40 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
            return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    private string PathOf(string id)
    {
        // The id becomes a file name: only what a generated identifier is made of is accepted.
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid changeset id.", nameof(id));
        return Path.Combine(_directory, id + ".json");
    }
}

internal static class ChangesetJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Write(Changeset changeset) => JsonSerializer.Serialize(changeset, Options);

    public static Changeset Read(string json) => JsonSerializer.Deserialize<Changeset>(json, Options) ?? throw new JsonException("Empty changeset file.");
}
