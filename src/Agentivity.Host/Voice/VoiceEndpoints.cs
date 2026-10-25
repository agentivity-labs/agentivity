using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Voice;

/// <summary>
/// Voice dictation endpoints — server-side transcription for the chat input's mic button.
/// Routes: /api/v1/voice
///
/// Deliberately OpenAI Whisper only for now (via the existing <see cref="SpeechToTextService"/>,
/// already used by the workflow node of the same name) — no local/offline inference. If a
/// platform credential isn't configured, voice is off in the app: the client is expected to
/// check <c>/voice/status</c> before ever showing the mic button, and this file's own
/// not-configured checks are a defensive backstop, not the primary UX.
/// </summary>
public static class VoiceEndpoints
{
    private const long MaxUploadBytes = 25 * 1024 * 1024; // 25 MB — comfortably covers several minutes of 16-bit mono WAV dictation.

    public static IEndpointRouteBuilder MapVoiceApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet(
            "/voice/status",
            async (AppSettingsService appSettingsService, CancellationToken ct) =>
            {
                AppSettings appSettings = await appSettingsService.GetAsync(ct);
                return Results.Ok(new { available = IsConfigured(appSettings.SpeechToText) });
            }
        )
            .WithSummary("Check whether voice dictation is available");

        v1.MapPost(
            "/voice/transcribe",
            async (HttpContext httpContext, AppSettingsService appSettingsService, SpeechToTextService speechToTextService, CancellationToken ct) =>
            {
                AppSettings appSettings = await appSettingsService.GetAsync(ct);
                SpeechToTextSettings settings = appSettings.SpeechToText;
                if (!IsConfigured(settings))
                {
                    return ApiError(StatusCodes.Status503ServiceUnavailable, code: "stt_not_configured", userMessage: "Voice transcription isn't configured.", detail: "No speech-to-text credentials are configured in Administration > Credentials.");
                }

                httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxUploadBytes;

                if (!httpContext.Request.HasFormContentType)
                {
                    return ApiValidationError(code: "audio_required", userMessage: "An audio file is required.", detail: "Expected multipart/form-data with an 'audio' file field.");
                }

                var form = await httpContext.Request.ReadFormAsync(ct);
                IFormFile? audio = form.Files["audio"];
                if (audio is null || audio.Length == 0)
                {
                    return ApiValidationError(code: "audio_required", userMessage: "An audio file is required.", detail: "The 'audio' form field is missing or empty.");
                }

                string? language = form["language"].FirstOrDefault();

                await using var stream = new MemoryStream();
                await audio.CopyToAsync(stream, ct);

                try
                {
                    TranscriptionResult result = await speechToTextService.TranscribeAsync(settings.CredentialsId, stream.ToArray(), audio.FileName, language, contextPrompt: null, ct);
                    return Results.Ok(new { text = result.Text, language = result.Language, durationSeconds = result.DurationSeconds });
                }
                catch (Exception ex)
                {
                    return ApiError(StatusCodes.Status503ServiceUnavailable, code: "stt_transcription_failed", userMessage: "Voice transcription failed.", detail: ex.Message);
                }
            }
        )
            .WithSummary("Transcribe a recorded audio clip to text");

        return routes;
    }

    private static bool IsConfigured(SpeechToTextSettings settings) =>
        settings.Enabled && !string.IsNullOrWhiteSpace(settings.CredentialsId) && settings.CredentialsId != "invalid_credential_id";
}
