using Agentivity.Infrastructure.Core.Diagnostics;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search;

public static class WebSearchDebugTrace
{
    public static DebugTraceItem Enrich(DebugTraceItem trace, string originalQuery, string effectiveQuery, string? requestUrl = null, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

        string normalizedOriginalQuery = string.IsNullOrWhiteSpace(originalQuery) ? string.Empty : originalQuery.Trim();
        string normalizedEffectiveQuery = string.IsNullOrWhiteSpace(effectiveQuery) ? normalizedOriginalQuery : effectiveQuery.Trim();
        string? normalizedRequestUrl = string.IsNullOrWhiteSpace(requestUrl) ? null : requestUrl.Trim();

        var children = new List<DebugTraceItem> { new("query", "Original query", normalizedOriginalQuery, DebugTraceStatus.Info) };

        if (!string.Equals(normalizedOriginalQuery, normalizedEffectiveQuery, StringComparison.Ordinal))
        {
            children.Add(new DebugTraceItem("query", "Effective query", normalizedEffectiveQuery, DebugTraceStatus.Info));
        }
        else
        {
            children.Add(new DebugTraceItem("query", "Query sent to the provider", normalizedEffectiveQuery, DebugTraceStatus.Info));
        }

        if (!string.IsNullOrWhiteSpace(normalizedRequestUrl))
        {
            children.Add(new DebugTraceItem("request_url", "Request URL", normalizedRequestUrl, DebugTraceStatus.Info));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            children.Add(new DebugTraceItem("error", "Error", error.Trim(), DebugTraceStatus.Error));
        }

        if (trace.Children is { Count: > 0 })
        {
            children.AddRange(trace.Children);
        }

        var fields = new List<DebugTraceField> { new("Original query", normalizedOriginalQuery), new("Effective query", normalizedEffectiveQuery) };

        if (!string.IsNullOrWhiteSpace(normalizedRequestUrl))
        {
            fields.Add(new DebugTraceField("Request URL", normalizedRequestUrl));
        }

        if (trace.Fields is { Count: > 0 })
        {
            fields.AddRange(trace.Fields);
        }

        return trace with
        {
            Fields = fields,
            Children = children,
        };
    }
}
