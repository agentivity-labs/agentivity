using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Media;

/// <summary>
/// Speech-to-text node — transcribes an audio file (local path or http(s) URL) via the OpenAI
/// Whisper API. Reuses the "openai" credentials template: Whisper is billed on the same account
/// as chat completions, no dedicated credentials needed.
/// </summary>
public sealed class SpeechToTextNode : ToolNode
{
    private readonly SpeechToTextService _service;

    public SpeechToTextNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<SpeechToTextNode> logger)
        : base("ai.media.speech_to_text")
    {
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _service = new SpeechToTextService(credentialsService, httpClientFactory, logger ?? throw new ArgumentNullException(nameof(logger)));

        DefineMetadata("Speech to Text", "Transcribes an audio file to text using OpenAI Whisper.", "ai.media");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "openai", "OpenAI credentials (Whisper uses the same account as chat models)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });

        DefineInput("audio_source", ArgumentType.Text, "Local file path or http(s) URL to the audio file", ui: new ArgumentUiMetadata { Group = "Audio", Order = 20 });
        DefineInput("language", ArgumentType.Text, "ISO-639-1 language hint (leave empty for auto-detection)", "", ui: new ArgumentUiMetadata { Group = "Audio", Order = 30 });
        DefineInput(
            "context_prompt",
            ArgumentType.Text,
            "Optional context to improve accuracy (proper nouns, jargon, expected style)",
            "",
            ui: new ArgumentUiMetadata { Group = "Audio", Order = 40, Multiline = true, Rows = 3 }
        );

        DefineOutput("text", ArgumentType.Text, "Transcribed text");
        DefineOutput("language", ArgumentType.Text, "Detected language");
        DefineOutput("duration_seconds", ArgumentType.Number, "Audio duration in seconds");

        DefineIcon(NodeIcon.Material(0xe029, "#5b55e8"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        services.PublishStatus("Transcribing audio...");

        TranscriptionResult result = await _service
            .TranscribeAsync(credentialsId: args.RequireText("credentials"), audioSource: args.RequireText("audio_source"), language: args.GetText("language"), contextPrompt: args.GetText("context_prompt"), cancellationToken)
            .ConfigureAwait(false);

        services.PublishStatus("Transcription completed");

        return NodeResult.Create().AddText("text", result.Text).AddText("language", result.Language ?? string.Empty).AddNumber("duration_seconds", result.DurationSeconds ?? 0);
    }
}
