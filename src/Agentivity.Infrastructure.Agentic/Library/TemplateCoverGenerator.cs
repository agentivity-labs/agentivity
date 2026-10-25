using Agentivity.Infrastructure.Core.ImageGeneration;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Options;
using CredentialModel = Agentivity.Domain.Credentials.Credentials;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>What a cover is drawn from: what the Template is, in the author's words.</summary>
public sealed record TemplateCoverBrief(string Name, string? Tagline, string? Description, string? Accent = null, string? Quality = null, int? Variation = null);

/// <summary>A cover picture ready to embed: <c>data:image/webp;base64,...</c>, at most <see cref="TemplateExporter.MaxCoverBytes"/> bytes.</summary>
public sealed record TemplateCover(string DataUri, long SizeBytes);

/// <summary>A connection a cover can be drawn with: one that leads to OpenAI and holds a key.</summary>
public sealed record TemplateCoverConnection(string Id, string Name);

public sealed class TemplateCoverException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Draws the cover picture of a Template with the image model the user is connected to (an OpenAI connection, or the server's own
/// key). Every cover is drawn in the same house style — <see cref="Style"/> — so the Templates of a catalog look like one family
/// and sit well on the Agentivity site (pearl ground, flat pastel shapes with deep navy details, one yellow accent); only the subject changes. The subject is always the THEME of the
/// Template (a travel planner is drawn with a plane and a beach, not with its team), so the covers say at a glance what each one is for.
/// </summary>
public sealed class TemplateCoverGenerator(IImageGenerationClient images, CredentialsService credentials, IOptions<OpenAIImageOptions> options)
{
    /// <summary>The look shared by every Template cover. Change it here and all new covers follow.</summary>
    public const string Style =
        "A polished, airy flat vector illustration for a software catalog card, landscape 3:2, in the visual language of the Agentivity website. "
        + "The whole frame is filled by an opaque, very light cool-grey background (#F4F5FA) with a fine regular grid of small pale dots; never transparent, never a checkerboard. "
        + "Clean rounded geometry, crisp edges, soft pastel fills (lavender #D9D4F5, sage green #CFE3D4, pale warm yellow #FFE9A8, soft sky blue #D6ECFA) with deep navy (#0D1B35) for outlines and small details. "
        + "Very soft, subtle drop shadows under the objects. A single small accent of vivid yellow (#FFC83D). Calm, premium, friendly, full of clear detail, objects drawn large so they fill most of the frame. "
        + "No text, no letters, no numbers, no logos, no people, no faces, no photo realism, no dark background.";

    /// <summary>
    /// How the subject is staged. One is picked from the name of the Template (and the number of the attempt), so two Templates do not
    /// share the same layout and "draw again" gives another one.
    /// </summary>
    private static readonly string[] Compositions =
    [
        "Staging: one large hero object in the center, a few smaller related objects floating around it.",
        "Staging: a small isometric scene on a floating rounded platform, with the related objects standing on it.",
        "Staging: a layered scene with a horizon line, a big shape in the background and the related objects in front.",
        "Staging: a flat-lay seen from above, the related objects neatly arranged on a soft rounded surface.",
        "Staging: the related objects along a gentle diagonal, from small at the bottom left to large at the top right.",
        "Staging: a big rounded shape framing the scene, the related objects overlapping its edge.",
    ];

    /// <summary>
    /// What the picture is about. The model is asked to find, in the Template, the real-world subject people associate with it, and
    /// to draw it with concrete, recognisable objects, never the team or the software behind it.
    /// </summary>
    private const string Subject =
        "SUBJECT: illustrate the THEME of the use case described below, not the software that does it. Decide what a person would immediately associate with it and draw 3 to 6 concrete, specific, imaginative, recognisable real-world objects, places or scenes of that theme, composed as one lively picture "
        + "(examples: a travel planner is an airplane, a suitcase, a palm tree on a beach and a map pin; customer support is a headset, speech bubbles and a ticket; recruiting is a CV sheet, a magnifier and a desk plant; invoicing is a receipt, a coin stack and a calculator; content marketing is a megaphone, a pen and a calendar). "
        + "Do NOT draw a team, an org chart, nodes, hexagons, robots, computers or screens unless the theme itself is about them.";

    /// <summary>The accent colors an author can pick: the house style stays, only its one touch of color changes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Accents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["yellow"] = "vivid yellow (#FFC83D)",
        ["lavender"] = "vivid violet (#6D00F5)",
        ["sage"] = "fresh mint green (#00C896)",
        ["sky"] = "bright cyan (#00CFFF)",
    };

    /// <summary>The detail levels an author can pick, as the image model names them.</summary>
    public static readonly IReadOnlyList<string> Qualities = ["low", "medium", "high"];

    private const int MaxAttempts = 3;
    private static readonly int[] CompressionSteps = [70, 45, 25];

    /// <summary>The prompt for a Template: the shared style, then the subject the picture should suggest.</summary>
    public static string BuildPrompt(TemplateCoverBrief brief)
    {
        var about = string.Join(". ", new[] { brief.Name, brief.Tagline, brief.Description }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));
        if (about.Length > 500)
        {
            about = about[..500];
        }

        var accent = brief.Accent is { } a && Accents.TryGetValue(a, out var color) ? $" For the single accent use {color} instead of yellow." : string.Empty;
        var composition = Compositions[(Stable(brief.Name) + Math.Max(0, brief.Variation ?? 0)) % Compositions.Length];
        return $"{Style}{accent} {Subject} {composition} USE CASE: {about}.";
    }

    // A hash that does not change between runs, unlike string.GetHashCode.
    private static int Stable(string text)
    {
        var hash = 17;
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            hash = unchecked((hash * 31) + c);
        }

        return Math.Abs(hash % 10_000);
    }

    /// <summary>What to say when nothing can draw: the author needs an OpenAI account.</summary>
    public const string NeedsOpenAiAccount = "Drawing a cover needs an OpenAI account: add a connection to OpenAI (api.openai.com) in Administration > Credentials.";

    // Where a connection of an OpenAI type leads when it names no address.
    private const string OpenAiAddress = "https://api.openai.com/v1";
    private static readonly string[] OpenAiTypes = ["openai", "openai_image"];

    /// <summary>Whether a cover can be drawn: a connection that leads to OpenAI, or a key in the server's own configuration.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => (await ConnectionsAsync(cancellationToken).ConfigureAwait(false)).Count > 0 || !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    /// <summary>
    /// The connections a cover can be drawn with, whatever their type: those whose address is OpenAI's (api.openai.com) and that hold
    /// a key. The type of a connection says the format it speaks, not who answers: one of an OpenAI type may lead to another service.
    /// </summary>
    public async Task<IReadOnlyList<TemplateCoverConnection>> ConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await credentials.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(LeadsToOpenAi).Select(c => new TemplateCoverConnection(c.Id, c.Name))];
    }

    private static bool LeadsToOpenAi(CredentialModel credential)
    {
        if (string.IsNullOrWhiteSpace(credential.GetField("api_key")))
            return false;
        var address = credential.GetField("base_url")?.Trim();
        if (string.IsNullOrEmpty(address))
            address = OpenAiTypes.Contains(credential.TemplateId, StringComparer.OrdinalIgnoreCase) ? OpenAiAddress : null;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Draws the cover with the connection <paramref name="credentialId"/> (one of <see cref="ConnectionsAsync"/>), or, when none is
    /// given, the first of them, or else the server's own key.
    /// </summary>
    public async Task<TemplateCover> GenerateAsync(TemplateCoverBrief brief, string? credentialId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(brief.Name))
        {
            throw new TemplateCoverException("name_required", "Give the Template a name first: the picture is drawn from it.");
        }

        var usable = await ConnectionsAsync(cancellationToken).ConfigureAwait(false);
        TemplateCoverConnection? credential;
        if (!string.IsNullOrWhiteSpace(credentialId))
        {
            credential = usable.FirstOrDefault(c => c.Id == credentialId);
            if (credential is null)
            {
                var named = (await credentials.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == credentialId)?.Name ?? credentialId;
                throw new TemplateCoverException("not_an_openai_connection", $"The connection \"{named}\" does not lead to OpenAI (api.openai.com), or holds no key: a cover is drawn by OpenAI's image model.");
            }
        }
        else
        {
            credential = usable.FirstOrDefault();
        }

        if (credential is null && string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            throw new TemplateCoverException("no_image_provider", NeedsOpenAiAccount);
        }

        var quality = brief.Quality is { } q && Qualities.Contains(q, StringComparer.OrdinalIgnoreCase) ? q.ToLowerInvariant() : "medium";
        var prompt = BuildPrompt(brief);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = await images
                .GenerateAsync(
                    new ImageGenerationRequest
                    {
                        Prompt = prompt,
                        CredentialsId = credential?.Id,
                        Size = "1536x1024",
                        Quality = quality,
                        Background = "opaque",
                        Count = 1,
                        OutputCompression = CompressionSteps[attempt],
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new TemplateCoverException("generation_failed", result.Error ?? "The picture could not be drawn.");
            }

            var image = result.Images.FirstOrDefault();
            if (image is null || image.Metadata?.ContainsKey("placeholder") == true)
            {
                throw new TemplateCoverException("no_image_provider", "No image model answered: check the OpenAI connection.");
            }

            // A cover travels inside the file: draw again, more compressed, when it is too heavy.
            if (image.Data.Length <= TemplateExporter.MaxCoverBytes)
            {
                return new TemplateCover($"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}", image.Data.Length);
            }
        }

        throw new TemplateCoverException("cover_too_large", $"The picture stays above {TemplateExporter.MaxCoverBytes / 1024} KB. Try again, or choose a picture of your own.");
    }
}
