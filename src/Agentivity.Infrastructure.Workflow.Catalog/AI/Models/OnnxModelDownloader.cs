using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.AI.Models;

/// <summary>
/// Helper to download ONNX models from HuggingFace
/// </summary>
public static class OnnxModelDownloader
{
    private const string HuggingFaceBaseUrl = "https://huggingface.co";

    public static async Task<bool> EnsureModelDownloadedAsync(string modelId, string modelFileName, string localPath, ILogger? logger = null)
    {
        if (File.Exists(localPath))
        {
            logger?.LogInformation("Model already exists at {Path}", localPath);
            return true;
        }

        logger?.LogInformation("Downloading model {ModelId}/{FileName}...", modelId, modelFileName);

        string? directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        string downloadUrl = $"{HuggingFaceBaseUrl}/{modelId}/resolve/main/{modelFileName}";

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            HttpResponseMessage response = await httpClient.GetAsync(downloadUrl);
            _ = response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);

            logger?.LogInformation("Model downloaded successfully to {Path} ({Size} bytes)", localPath, new FileInfo(localPath).Length);

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to download model from {Url}", downloadUrl);
            return false;
        }
    }

    public static async Task<bool> EnsureSentenceTransformerModelAsync(string modelName, string basePath, ILogger? logger = null)
    {
        // basePath is now the full path to the models directory (e.g., <app root>/_cache/llmmodels)
        string modelPath = Path.Combine(basePath, "sentence-transformers", modelName);

        // Le modèle ONNX est dans le sous-dossier onnx/ sur HuggingFace
        string onnxFile = Path.Combine(modelPath, "model.onnx");
        string vocabFile = Path.Combine(modelPath, "vocab.txt");
        string tokenizerFile = Path.Combine(modelPath, "tokenizer.json");

        string modelId = $"sentence-transformers/{modelName}";

        // Télécharger depuis le dossier onnx/ sur HuggingFace
        bool onnxOk = await EnsureModelDownloadedAsync(modelId, "onnx/model.onnx", onnxFile, logger);
        bool vocabOk = await EnsureModelDownloadedAsync(modelId, "vocab.txt", vocabFile, logger);
        bool tokenizerOk = await EnsureModelDownloadedAsync(modelId, "tokenizer.json", tokenizerFile, logger);

        return onnxOk && vocabOk && tokenizerOk;
    }
}
