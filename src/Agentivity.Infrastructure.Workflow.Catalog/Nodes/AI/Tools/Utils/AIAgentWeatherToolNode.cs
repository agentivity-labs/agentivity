using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Utils;

/// <summary>
/// Simple current weather tool for AI agents backed by the free Open-Meteo APIs.
/// </summary>
public sealed class AIAgentWeatherToolNode : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ToolName = "get_current_weather";
    private const string GeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastBaseUrl = "https://api.open-meteo.com/v1/forecast";

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "location": {
      "type": "string",
      "description": "City or location name to look up, for example 'Paris' or 'New York'"
    },
    "country_code": {
      "type": "string",
      "description": "Optional ISO 3166-1 alpha-2 country code to disambiguate the location, for example 'FR' or 'US'"
    },
    "temperature_unit": {
      "type": "string",
      "enum": ["celsius", "fahrenheit"],
      "description": "Optional temperature unit for the weather response"
    }
  },
  "required": ["location"],
  "additionalProperties": false
}
""";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AIAgentWeatherToolNode> _logger;

    public AIAgentWeatherToolNode(IHttpClientFactory httpClientFactory, ILogger<AIAgentWeatherToolNode> logger)
        : base("ai.tools.utils.weather")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Weather Tool", "Retrieves the current weather for a location using the free Open-Meteo API.", "ai.tools.utils");
        DefineEffects(NodeEffects.ReadsExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Weather Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([LlmToolDefinitionJson.Create(ToolName, "Get the current weather for a city or named location.", InputSchema)]);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true);
        }

        if (!arguments.TryGetProperty("location", out JsonElement locationElement) || locationElement.ValueKind != JsonValueKind.String)
        {
            return McpToolResultJson.CreateTextResult("The weather tool requires a string 'location' argument.", true);
        }

        string? location = locationElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            return McpToolResultJson.CreateTextResult("The 'location' argument cannot be empty.", true);
        }

        string? countryCode = arguments.TryGetProperty("country_code", out JsonElement countryCodeElement) && countryCodeElement.ValueKind == JsonValueKind.String ? countryCodeElement.GetString()?.Trim()?.ToUpperInvariant() : null;
        string temperatureUnit = arguments.TryGetProperty("temperature_unit", out JsonElement temperatureUnitElement) && temperatureUnitElement.ValueKind == JsonValueKind.String ? ParseTemperatureUnit(temperatureUnitElement.GetString()) : "celsius";

        try
        {
            using JsonDocument geocodingDocument = await GetJsonDocumentAsync(BuildGeocodingUri(location, countryCode), cancellationToken).ConfigureAwait(false);
            ResolvedLocation? resolvedLocation = TryResolveLocation(geocodingDocument.RootElement, countryCode);
            if (resolvedLocation is null)
            {
                return McpToolResultJson.CreateTextResult($"No weather location match was found for '{location}'.", true);
            }

            using JsonDocument forecastDocument = await GetJsonDocumentAsync(BuildForecastUri(resolvedLocation.Value, temperatureUnit), cancellationToken).ConfigureAwait(false);
            JsonElement root = forecastDocument.RootElement;

            if (!root.TryGetProperty("current", out JsonElement current))
            {
                return McpToolResultJson.CreateTextResult($"The weather service did not return current weather data for '{resolvedLocation.Value.Name}'.", true);
            }

            int weatherCode = current.TryGetProperty("weather_code", out JsonElement weatherCodeElement) && weatherCodeElement.TryGetInt32(out int parsedWeatherCode) ? parsedWeatherCode : -1;
            double? temperature = current.TryGetProperty("temperature_2m", out JsonElement temperatureElement) && temperatureElement.TryGetDouble(out double parsedTemperature) ? parsedTemperature : (double?)null;
            double? windSpeed = current.TryGetProperty("wind_speed_10m", out JsonElement windSpeedElement) && windSpeedElement.TryGetDouble(out double parsedWindSpeed) ? parsedWindSpeed : (double?)null;
            string? observedAt = current.TryGetProperty("time", out JsonElement timeElement) ? timeElement.GetString() : null;
            bool? isDay = current.TryGetProperty("is_day", out JsonElement isDayElement) && isDayElement.TryGetInt32(out int parsedIsDay) ? parsedIsDay == 1 : (bool?)null;
            string weatherDescription = DescribeWeatherCode(weatherCode);
            string? timezone = root.TryGetProperty("timezone", out JsonElement timezoneElement) ? timezoneElement.GetString() : resolvedLocation.Value.Timezone;

            var payload = new
            {
                location = new
                {
                    name = resolvedLocation.Value.Name,
                    country = resolvedLocation.Value.Country,
                    country_code = resolvedLocation.Value.CountryCode,
                    latitude = resolvedLocation.Value.Latitude,
                    longitude = resolvedLocation.Value.Longitude,
                    timezone,
                },
                current_weather = new
                {
                    observed_at = observedAt,
                    temperature = temperature,
                    temperature_unit = temperatureUnit,
                    wind_speed_kmh = windSpeed,
                    weather_code = weatherCode,
                    weather_description = weatherDescription,
                    is_day = isDay,
                },
                summary = BuildSummary(resolvedLocation.Value.Name, temperature, temperatureUnit, weatherDescription, windSpeed),
            };

            return McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI agent weather tool failed for location {Location}", location);
            return McpToolResultJson.CreateTextResult($"Failed to retrieve weather for '{location}': {ex.Message}", true);
        }
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(content);
    }

    private static string BuildGeocodingUri(string location, string? countryCode)
    {
        string url = $"{GeocodingBaseUrl}?name={Uri.EscapeDataString(location)}&count=5&language=en&format=json";
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            url += $"&countryCode={Uri.EscapeDataString(countryCode)}";
        }

        return url;
    }

    private static string BuildForecastUri(ResolvedLocation location, string temperatureUnit) => $"{ForecastBaseUrl}?latitude={location.Latitude}&longitude={location.Longitude}&current=temperature_2m,weather_code,wind_speed_10m,is_day&timezone=auto&temperature_unit={temperatureUnit}";

    private static ResolvedLocation? TryResolveLocation(JsonElement root, string? countryCode)
    {
        if (!root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement candidate in results.EnumerateArray())
        {
            if (!TryReadLocation(candidate, out ResolvedLocation location))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(countryCode) || string.Equals(location.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
            {
                return location;
            }
        }

        foreach (JsonElement candidate in results.EnumerateArray())
        {
            if (TryReadLocation(candidate, out ResolvedLocation location))
            {
                return location;
            }
        }

        return null;
    }

    private static bool TryReadLocation(JsonElement candidate, out ResolvedLocation location)
    {
        location = default;

        if (
            !candidate.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || !candidate.TryGetProperty("latitude", out JsonElement latitudeElement)
            || !latitudeElement.TryGetDouble(out double latitude)
            || !candidate.TryGetProperty("longitude", out JsonElement longitudeElement)
            || !longitudeElement.TryGetDouble(out double longitude)
        )
        {
            return false;
        }

        location = new ResolvedLocation(
            nameElement.GetString() ?? string.Empty,
            candidate.TryGetProperty("country", out JsonElement countryElement) ? countryElement.GetString() ?? string.Empty : string.Empty,
            candidate.TryGetProperty("country_code", out JsonElement countryCodeElement) ? countryCodeElement.GetString() ?? string.Empty : string.Empty,
            latitude,
            longitude,
            candidate.TryGetProperty("timezone", out JsonElement timezoneElement) ? timezoneElement.GetString() ?? string.Empty : string.Empty
        );
        return true;
    }

    private static string ParseTemperatureUnit(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "fahrenheit" => "fahrenheit",
            _ => "celsius",
        };

    private static string BuildSummary(string location, double? temperature, string temperatureUnit, string weatherDescription, double? windSpeed)
    {
        string temperatureText = temperature.HasValue ? $"{temperature.Value:0.#}°{(temperatureUnit == "fahrenheit" ? "F" : "C")}" : "unknown temperature";
        string windText = windSpeed.HasValue ? $" with wind around {windSpeed.Value:0.#} km/h" : string.Empty;

        return $"Current weather in {location}: {weatherDescription}, {temperatureText}{windText}.";
    }

    private static string DescribeWeatherCode(int code) =>
        code switch
        {
            0 => "clear sky",
            1 => "mainly clear",
            2 => "partly cloudy",
            3 => "overcast",
            45 or 48 => "fog",
            51 or 53 or 55 => "drizzle",
            56 or 57 => "freezing drizzle",
            61 or 63 or 65 => "rain",
            66 or 67 => "freezing rain",
            71 or 73 or 75 or 77 => "snow",
            80 or 81 or 82 => "rain showers",
            85 or 86 => "snow showers",
            95 => "thunderstorm",
            96 or 99 => "thunderstorm with hail",
            _ => code >= 0 ? "unknown conditions" : "unknown weather",
        };

    private readonly record struct ResolvedLocation(string Name, string Country, string CountryCode, double Latitude, double Longitude, string Timezone);
}
