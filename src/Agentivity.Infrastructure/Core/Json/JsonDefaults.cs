using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// Centralized JSON serializer options for the entire application.
/// Provides consistent configuration with support for comments and trailing commas.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Default JSON options for general use (API, workflows, etc.).
    /// - Case-insensitive property matching
    /// - Supports JSON comments
    /// - Allows trailing commas
    /// - camelCase property naming
    /// </summary>
    public static JsonSerializerOptions Default =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
        };

    /// <summary>
    /// JSON options for workflow definitions with smart JSON string handling.
    /// Automatically detects and parses JSON strings into native objects,
    /// eliminating multi-level JSON escaping (JSON-in-JSON).
    ///
    /// <para>Features:</para>
    /// <list type="bullet">
    ///   <item>Case-insensitive property matching</item>
    ///   <item>Smart detection of embedded JSON in string values</item>
    ///   <item>Preserves template expressions ({{ }}) as strings</item>
    ///   <item>Indented output for readability</item>
    /// </list>
    /// </summary>
    public static JsonSerializerOptions Workflow =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new SmartJsonStringConverterFactory() },
        };

    /// <summary>
    /// JSON options for configuration files.
    /// Same as Default but with indented output for readability.
    /// </summary>
    public static JsonSerializerOptions Configuration =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };

    /// <summary>
    /// JSON options for snake_case (used by DSL).
    /// - snake_case property naming
    /// - Supports comments and trailing commas
    /// - Ignores null values when writing
    /// </summary>
    public static JsonSerializerOptions SnakeCase =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

    /// <summary>
    /// JSON options for web APIs (System.Text.Json.JsonSerializerDefaults.Web).
    /// - camelCase
    /// - Case-insensitive
    /// - Supports comments and trailing commas
    /// </summary>
    public static JsonSerializerOptions Web => new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
