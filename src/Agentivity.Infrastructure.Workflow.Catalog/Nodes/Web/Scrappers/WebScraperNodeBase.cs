using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Web.Scrappers;

/// <summary>
/// Base class for web scraper resource nodes.
/// </summary>
public abstract class WebScraperNodeBase : ResourceNode
{
    protected WebScraperNodeBase(string nodeType)
        : base(nodeType) { }

    protected void DefineCommonMetadata(string displayName, string description, string whenToUse)
    {
        DefineMetadata(displayName, description, "web.scrappers", whenToUse: whenToUse);
        DefineEffects(NodeEffects.ReadsExternal);
    }
}
