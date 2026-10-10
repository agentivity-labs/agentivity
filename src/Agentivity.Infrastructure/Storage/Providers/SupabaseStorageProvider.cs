using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Storage.Providers;

/// <summary>
/// Supabase storage provider using Supabase database (PostgREST API).
/// Stores key-value pairs in a table named 'storage_items'.
///
/// Required table schema:
/// CREATE TABLE storage_items (
///   key TEXT PRIMARY KEY,
///   value TEXT NOT NULL,
///   created_at TIMESTAMPTZ DEFAULT NOW(),
///   updated_at TIMESTAMPTZ DEFAULT NOW(),
///   expires_at TIMESTAMPTZ,
///   metadata JSONB
/// );
/// </summary>
public sealed class SupabaseStorageProvider : IStorageProvider
{
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly string _tableName;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SupabaseStorageProvider> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ProviderName => "Supabase";

    public SupabaseStorageProvider(string supabaseUrl, string supabaseKey, string tableName, IHttpClientFactory httpClientFactory, ILogger<SupabaseStorageProvider> logger)
    {
        _supabaseUrl = supabaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(supabaseUrl));
        _supabaseKey = supabaseKey ?? throw new ArgumentNullException(nameof(supabaseKey));
        _tableName = tableName ?? "storage_items";
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient.BaseAddress = new Uri($"{_supabaseUrl}/rest/v1/");
        _httpClient.DefaultRequestHeaders.Add("apikey", _supabaseKey);
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_supabaseKey}");
        _httpClient.DefaultRequestHeaders.Add("Prefer", "return=representation");
    }

    public async ValueTask<StorageResult<string>> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        try
        {
            var url = $"{_tableName}?key=eq.{Uri.EscapeDataString(key)}&select=value,expires_at";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return StorageResult<string>.Fail($"HTTP {response.StatusCode}: {error}");
            }

            var items = await response.Content.ReadFromJsonAsync<List<StorageItem>>(_jsonOptions, cancellationToken);

            if (items == null || items.Count == 0)
            {
                return StorageResult<string>.Fail($"Key '{key}' not found");
            }

            var item = items[0];

            // Check expiration
            if (item.ExpiresAt.HasValue && item.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                // Delete expired item
                await DeleteAsync(key, cancellationToken);
                return StorageResult<string>.Fail($"Key '{key}' has expired");
            }

            return StorageResult<string>.Ok(item.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read key '{Key}' from Supabase storage", key);
            return StorageResult<string>.Fail(ex.Message);
        }
    }

    public async ValueTask<StorageResult> WriteAsync(string key, string value, StorageWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        try
        {
            var item = new StorageItem
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = options?.Expiration.HasValue == true ? DateTimeOffset.UtcNow.Add(options.Expiration.Value) : null,
                Metadata = options?.Metadata,
            };

            // Use upsert (INSERT ... ON CONFLICT UPDATE)
            var url = $"{_tableName}";
            var content = JsonContent.Create(item, options: _jsonOptions);
            content.Headers.Add("Prefer", "resolution=merge-duplicates");

            var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return StorageResult.Fail($"HTTP {response.StatusCode}: {error}");
            }

            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write key '{Key}' to Supabase storage", key);
            return StorageResult.Fail(ex.Message);
        }
    }

    public async ValueTask<StorageResult> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        try
        {
            var url = $"{_tableName}?key=eq.{Uri.EscapeDataString(key)}";
            var response = await _httpClient.DeleteAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return StorageResult.Fail($"HTTP {response.StatusCode}: {error}");
            }

            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete key '{Key}' from Supabase storage", key);
            return StorageResult.Fail(ex.Message);
        }
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        try
        {
            var url = $"{_tableName}?key=eq.{Uri.EscapeDataString(key)}&select=key,expires_at";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var items = await response.Content.ReadFromJsonAsync<List<StorageItem>>(_jsonOptions, cancellationToken);

            if (items == null || items.Count == 0)
            {
                return false;
            }

            var item = items[0];

            // Check expiration
            if (item.ExpiresAt.HasValue && item.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check existence of key '{Key}' in Supabase storage", key);
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<string>> ListKeysAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_tableName}?select=key,expires_at";

            // Apply pattern filter using PostgREST 'like' operator
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var postgresPattern = pattern.Replace("*", "%");
                url += $"&key=like.{Uri.EscapeDataString(postgresPattern)}";
            }

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to list keys from Supabase storage: HTTP {StatusCode}", response.StatusCode);
                return Array.Empty<string>();
            }

            var items = await response.Content.ReadFromJsonAsync<List<StorageItem>>(_jsonOptions, cancellationToken);

            if (items == null)
            {
                return Array.Empty<string>();
            }

            var now = DateTimeOffset.UtcNow;

            // Filter out expired entries
            var validKeys = items.Where(item => !item.ExpiresAt.HasValue || item.ExpiresAt.Value >= now).Select(item => item.Key).ToList();

            return validKeys;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list keys from Supabase storage");
            return Array.Empty<string>();
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace", nameof(key));
        }

        if (key.Length > 255)
        {
            throw new ArgumentException("Key cannot exceed 255 characters", nameof(key));
        }
    }

    private sealed class StorageItem
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
