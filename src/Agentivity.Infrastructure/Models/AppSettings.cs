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
