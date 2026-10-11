namespace Agentivity.Infrastructure.Models;

/// <summary>
/// Application-level settings (editable via UI).
/// Stored in _data/config/settings.json.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public AIAssistantSettings AIAssistant { get; set; } = new();
    public BuilderAssistantSettings Assistant { get; set; } = new();
    public SpeechToTextSettings SpeechToText { get; set; } = new();
}

public sealed class AIAssistantSettings
{
    /// <summary>
    /// Credentials ID to use for AI Assistant (e.g., "cred_lmstudio_001").
    /// </summary>
    public string CredentialsId { get; set; } = "invalid_credential_id";

    /// <summary>
    /// Whether the AI Assistant is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The assistant that builds agents, teams and workflows with the person. Separate from <see cref="AIAssistantSettings"/>
/// (the older in-editor helper): choosing a model for one does not change the other.
/// </summary>
public sealed class BuilderAssistantSettings
{
    /// <summary>
    /// Model connection (credential id) the assistant talks to. Empty: the first model connection that holds a key.
    /// </summary>
    public string CredentialsId { get; set; } = "";

    /// <summary>Whether the assistant is available.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class SpeechToTextSettings
{
    /// <summary>
    /// Credentials ID used to transcribe chat dictation (OpenAI-compatible, whisper-1).
    /// Resolved once at the platform level since transcription happens at compose-time,
    /// before any agent (and its own credential) is necessarily chosen.
    /// </summary>
    public string CredentialsId { get; set; } = "invalid_credential_id";

    /// <summary>
    /// Whether voice dictation is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
