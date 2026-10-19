using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.AI.Models;

/// <summary>
/// Helper to download ONNX models from HuggingFace
/// </summary>
public static class OnnxModelDownloader
{
    private const string HuggingFaceBaseUrl = "https://huggingface.co";

    public static async Task<bool> EnsureModelDownloadedAsync(string modelId, string modelFileName, string localPath, ILogger? logger = null) =>
        await DownloadAsync(modelId, modelFileName, localPath, logger).ConfigureAwait(false) is null;

    public static async Task<bool> EnsureSentenceTransformerModelAsync(string modelName, string basePath, ILogger? logger = null) =>
        await DownloadSentenceTransformerAsync(modelName, basePath, logger).ConfigureAwait(false) is null;

    /// <summary>
    /// Downloads the files of a sentence-transformers model that are not there yet. Null when they all are; otherwise why the
    /// first one that could not be fetched was not, in plain words (the others are not tried: they would fail the same way).
    /// </summary>
    internal static async Task<string?> DownloadSentenceTransformerAsync(string modelName, string basePath, ILogger? logger = null, HttpMessageHandler? network = null)
    {
        // basePath is now the full path to the models directory (e.g., <app root>/_cache/llmmodels)
        string modelPath = Path.Combine(basePath, "sentence-transformers", modelName);
        string modelId = $"sentence-transformers/{modelName}";

        // Le modèle ONNX est dans le sous-dossier onnx/ sur HuggingFace
        foreach (var (remote, local) in new[] { ("onnx/model.onnx", "model.onnx"), ("vocab.txt", "vocab.txt"), ("tokenizer.json", "tokenizer.json") })
        {
            if (await DownloadAsync(modelId, remote, Path.Combine(modelPath, local), logger, network).ConfigureAwait(false) is { } why)
                return why;
        }

        return null;
    }

    /// <summary>Downloads one file unless it is there. Null when it is; otherwise why it could not be fetched, in plain words.</summary>
    internal static async Task<string?> DownloadAsync(string modelId, string modelFileName, string localPath, ILogger? logger = null, HttpMessageHandler? network = null)
    {
        if (File.Exists(localPath))
        {
            logger?.LogInformation("Model already exists at {Path}", localPath);
            return null;
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
            using var httpClient = network is null ? new HttpClient() : new HttpClient(network, disposeHandler: false);
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            using HttpResponseMessage response = await httpClient.GetAsync(downloadUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return $"huggingface.co answered {(int)response.StatusCode} for {modelFileName}";

            await using (var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            logger?.LogInformation("Model downloaded successfully to {Path} ({Size} bytes)", localPath, new FileInfo(localPath).Length);

            return null;
        }
        catch (Exception ex)
        {
            // A file cut halfway would be taken for the model at the next start.
            if (File.Exists(localPath))
                File.Delete(localPath);
            // The detail is for whoever investigates; the reason, in words, is for the one warning the caller writes.
            logger?.LogDebug(ex, "Failed to download model from {Url}", downloadUrl);
            return Explain(ex);
        }
    }

    /// <summary>Why a download failed, in words someone who reads the log can act on.</summary>
    internal static string Explain(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return "the certificate of huggingface.co is not trusted here (an antivirus or a company proxy that inspects HTTPS connections is the usual cause)";
            if (e is SocketException)
                return "huggingface.co cannot be reached (no internet access, or a proxy that blocks it)";
        }

        return error is TaskCanceledException or TimeoutException ? "the download took too long" : error.Message;
    }
}
