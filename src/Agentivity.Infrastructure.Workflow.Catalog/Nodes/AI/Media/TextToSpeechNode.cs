using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Media;

/// <summary>
/// Text-to-speech node — synthesizes speech audio from text via the OpenAI TTS API. Reuses the
/// "openai" credentials template, same rationale as <see cref="SpeechToTextNode"/>: TTS is billed
/// on the same account as chat/Whisper. The audio counterpart of <see cref="AIImageGenNode"/> —
/// synthesize once, cache the bytes, return an <see cref="AudioRef"/> the same way image
/// generation returns an <c>ImageRef</c>.
/// </summary>
public sealed class TextToSpeechNode : ToolNode
{
    private readonly TextToSpeechService _service;
    private readonly AudioCacheService _cacheService;

    public TextToSpeechNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, AudioCacheService cacheService, ILogger<TextToSpeechNode> logger)
        : base("ai.media.text_to_speech")
    {
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _service = new TextToSpeechService(credentialsService, httpClientFactory, logger ?? throw new ArgumentNullException(nameof(logger)));

        DefineMetadata("Text to Speech", "Synthesizes speech audio from text using the OpenAI TTS API.", "ai.media");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "openai", "OpenAI credentials (TTS uses the same account as chat models)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });

        DefineInput(
            "text",
            ArgumentType.Text,
            "Text to synthesize",
            ui: new ArgumentUiMetadata { Group = "Speech", Order = 20, Multiline = true, Rows = 5 }
        );
        DefineChoice(
            "voice",
            [("alloy", "Alloy"), ("echo", "Echo"), ("fable", "Fable"), ("onyx", "Onyx"), ("nova", "Nova"), ("shimmer", "Shimmer")],
            "Voice",
            "alloy",
            ui: new ArgumentUiMetadata { Group = "Speech", Order = 30 }
        );
        DefineChoice("model", [("tts-1", "tts-1 (Fast)"), ("tts-1-hd", "tts-1-hd (Higher Quality)")], "Model", "tts-1", ui: new ArgumentUiMetadata { Group = "Speech", Order = 40 });
        DefineChoice("response_format", [("mp3", "MP3"), ("opus", "Opus"), ("aac", "AAC"), ("flac", "FLAC"), ("wav", "WAV")], "Audio format", "mp3", ui: new ArgumentUiMetadata { Group = "Speech", Order = 50 });

        DefineOutput("audio", ArgumentType.AudioRef, "Synthesized audio reference");

        DefineIcon(NodeIcon.Material(0xe050, "#5b55e8"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        services.PublishStatus("Synthesizing speech...");

        SynthesizedAudio audio = await _service
            .SynthesizeAsync(
                credentialsId: args.RequireText("credentials"),
                text: args.RequireText("text"),
                model: args.GetText("model") ?? "tts-1",
                voice: args.GetText("voice") ?? "alloy",
                responseFormat: args.GetText("response_format") ?? "mp3",
                cancellationToken
            )
            .ConfigureAwait(false);

        services.PublishStatus("Caching synthesized audio...");
        AudioRef audioRef = await _cacheService.CacheFromBytesAsync(audio.Bytes, audio.MimeType, durationSeconds: null, cancellationToken).ConfigureAwait(false);

        services.PublishStatus("Speech synthesis completed");

        return NodeResult.Create().AddAudioRef("audio", audioRef);
    }
}
