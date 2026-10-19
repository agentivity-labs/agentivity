namespace Agentivity.Infrastructure.Core.Llm.Knowledge;

/// <summary>Turns texts into vectors that represent their meaning: texts close in meaning give close vectors. Used inside the engine of the Knowledge Bases.</summary>
public interface ITextEmbedder
{
    /// <summary>One vector per text, in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
