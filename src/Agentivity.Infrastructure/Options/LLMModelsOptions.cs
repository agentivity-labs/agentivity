namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Configuration options for LLM models storage (ONNX, etc.)
/// </summary>
public sealed class LLMModelsOptions
{
    public const string SectionName = "LLMModels";

    /// <summary>
    /// Path where LLM models are stored (ONNX files, vocabularies, etc.)
    /// Relative to the solution root directory.
    /// </summary>
    public string StoragePath { get; set; } = "_cache/llmmodels/";
}
