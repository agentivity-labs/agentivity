using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>Background service that periodically cleans up expired audio files from the cache.</summary>
public sealed class AudioCacheCleanupService : BackgroundService
{
    private readonly AudioCacheService _cacheService;
    private readonly AudioCacheOptions _options;
    private readonly ILogger<AudioCacheCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval;

    public AudioCacheCleanupService(AudioCacheService cacheService, IOptions<AudioCacheOptions> options, ILogger<AudioCacheCleanupService> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options?.Value ?? new AudioCacheOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cleanupInterval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audio cache cleanup service started. Retention: {RetentionDays} days, Interval: {Interval}", _options.RetentionDays, _cleanupInterval);

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deletedCount = await _cacheService.CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);

                    if (deletedCount > 0)
                    {
                        var (fileCount, totalSize) = _cacheService.GetCacheStats();
                        _logger.LogInformation("Audio cache cleanup completed. Deleted: {DeletedCount}, Remaining: {FileCount} files ({TotalSizeMB:F2} MB)", deletedCount, fileCount, totalSize / (1024.0 * 1024.0));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during audio cache cleanup");
                }

                await Task.Delay(_cleanupInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected when the host stops while this worker is delaying between cleanups — not an error.
        }

        _logger.LogInformation("Audio cache cleanup service stopped");
    }
}
