namespace Agentivity.Infrastructure.Interactions;

public sealed class InteractionThreadStorageOptions
{
    public const string SectionName = "Chat";

    public string StoragePath { get; set; } = "_cache/chat";
}
