namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// One entry in a <see cref="LlmChatMessagesList"/>: an <see cref="LlmChatMessage"/> plus who wrote
/// it. <see cref="AuthorId"/> is what lets a reader tell "my own past turn" apart from "someone
/// else's" — see <see cref="LlmChatMessagesList"/> for why that distinction matters.
/// </summary>
public sealed record LlmChatMessageEntry(LlmChatMessage Message, string? AuthorId = null);

/// <summary>
/// Thread-safe, append-only list of <see cref="LlmChatMessage"/> entries.
/// Correct under concurrent writers: every write is serialized behind a lock, then the backing
/// list is replaced atomically, so every read observes a consistent, immutable snapshot — never a
/// torn list, regardless of how many writers call <see cref="Add"/> at once.
/// Generic — not tied to any orchestration topology, engine, or team concept.
/// </summary>
public sealed class LlmChatMessagesList
{
    private volatile List<LlmChatMessageEntry> _entries = [];
    private readonly object _writeLock = new();

    public IReadOnlyList<LlmChatMessageEntry> Entries => _entries;

    public void Add(LlmChatMessage message, string? authorId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_writeLock)
        {
            var updated = new List<LlmChatMessageEntry>(_entries) { new(message, authorId) };
            _entries = updated;
        }
    }
}
