using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Opaque, type-specific metadata attached to a persisted graph task state.
/// The graph engine treats this as a black box — it stores and restores it verbatim.
/// Orchestrators populate and consume their own concrete implementation so they can
/// reconstruct task definitions (e.g. which agent slot a task represents) on resume
/// without replaying the original execution.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public interface IGraphTaskMetadata { }
