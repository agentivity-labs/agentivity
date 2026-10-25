using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Domain.WordPress;

public sealed record WordPressMediaUploadRequest(string FilePath, string Title, string? AltText, string? Description, string? Caption);

public sealed record WordPressMediaUploadResult(int MediaId, string MediaUrl, JsonObject? RawResponse);

public sealed record WordPressPostRequest(string Title, IReadOnlyList<string> MetaTags, string MetaDescription, string Status, string? Content, JsonNode? ElementorData, IReadOnlyList<int>? Categories, int? FeaturedMediaId, string? Slug, bool UpdateFeaturedImageAfterPublish);

public sealed record WordPressPostResult(int PostId, string Permalink, bool FeaturedMediaUpdated, JsonObject? RawResponse);
