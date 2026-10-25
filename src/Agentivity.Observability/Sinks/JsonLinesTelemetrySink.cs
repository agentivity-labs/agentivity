using System.Text;
using System.Text.Json;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Options;
using Microsoft.Extensions.Options;

namespace Agentivity.Observability.Sinks;

public sealed class JsonLinesTelemetrySink(IOptions<JsonLinesTelemetrySinkOptions> options) : IAgentTelemetrySink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly JsonLinesTelemetrySinkOptions _options = options.Value;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_options.DirectoryPath);
        var filePath = Path.Combine(_options.DirectoryPath, $"{_options.FileNamePrefix}-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(envelope, SerializerOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(filePath, line + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
