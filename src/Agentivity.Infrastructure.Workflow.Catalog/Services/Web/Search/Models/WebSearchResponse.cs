using System.Text.Json.Serialization;
using Agentivity.Infrastructure.Core.Diagnostics;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;

public sealed record WebSearchResponse(string ProviderId, string ProviderDisplayName, string Query, string EffectiveQuery, IReadOnlyList<WebSearchResultItem> Results, [property: JsonIgnore] DebugTraceItem? Debug = null);
