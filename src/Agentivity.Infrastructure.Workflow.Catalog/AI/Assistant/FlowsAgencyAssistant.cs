using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Workflow.Services;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.AI.Assistant;

/// <summary>
/// The Agentivity AI Assistant - a platform-aware agent that helps users
/// design workflows, configure nodes, and understand the system.
/// </summary>
public sealed class FlowsAgencyAssistant
{
    private readonly AssistantKnowledgeBase _knowledgeBase;
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly AppSettingsService _appSettingsService;
    private readonly IAgUiWidgetBundleRegistry _bundleRegistry;
    private readonly ILogger<FlowsAgencyAssistant> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public FlowsAgencyAssistant(AssistantKnowledgeBase knowledgeBase, ILlmClientFactory llmClientFactory, AppSettingsService appSettingsService, IAgUiWidgetBundleRegistry bundleRegistry, ILogger<FlowsAgencyAssistant> logger)
    {
        _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
        _llmClientFactory = llmClientFactory ?? throw new ArgumentNullException(nameof(llmClientFactory));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        _bundleRegistry = bundleRegistry ?? throw new ArgumentNullException(nameof(bundleRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Chat with the assistant about the Agentivity platform.
    /// </summary>
    public async Task<AssistantResponse> ChatAsync(AssistantRequest request, CancellationToken cancellationToken = default)
    {
        // Get credentials from app settings
        AppSettings appSettings = await _appSettingsService.GetAsync(cancellationToken);
        string credentialsId = appSettings.AIAssistant.CredentialsId;

        _logger.LogInformation("Processing assistant request with credentials: {CredentialsId}, Message: {Message}", credentialsId, request.Message.Length > 100 ? request.Message[..100] + "..." : request.Message);

        try
        {
            // Create LLM client
            var llmClient = await _llmClientFactory.CreateAsync(credentialsId, cancellationToken).ConfigureAwait(false);

            // Build system prompt
            string systemPrompt = _knowledgeBase.GetSystemPrompt();

            // Add workflow context if provided
            if (!string.IsNullOrWhiteSpace(request.WorkflowContext))
            {
                systemPrompt += $"\n\n# CURRENT WORKFLOW CONTEXT\n```json\n{request.WorkflowContext}\n```";
            }

            // Build conversation
            string userPrompt = request.Message;
            if (request.History is not null && request.History.Count > 0)
            {
                string historyText = string.Join("\n\n", request.History.Select(h => $"{h.Role}: {h.Content}"));
                userPrompt = $"Previous conversation:\n{historyText}\n\nCurrent message: {request.Message}";
            }

            // Call LLM
            _logger.LogInformation("Sending request to LLM with {CharCount} characters in system prompt", systemPrompt.Length);

            OperationResult<LlmResponse> result = await llmClient.CompleteAsync(userPrompt, systemPrompt, temperature: 0.3, maxTokens: 2000, responseSchema: null, modelOverride: null, cancellationToken: cancellationToken);

            if (result.IsFailure || result.Value is null)
            {
                _logger.LogWarning("LLM completion failed: {Error}", result.Error);
                return new AssistantResponse { Success = false, Error = result.Error ?? "LLM completion failed." };
            }

            _logger.LogInformation("Assistant response generated successfully. Tokens used: {TokensUsed}", result.Value.TokensUsed);

            return new AssistantResponse
            {
                Success = true,
                Message = result.Value.Content,
                TokensUsed = result.Value.TokensUsed,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing assistant request.");
            return new AssistantResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Suggest nodes for a specific task.
    /// </summary>
    public async Task<NodeSuggestionResponse> SuggestNodesAsync(NodeSuggestionRequest request, CancellationToken cancellationToken = default)
    {
        string prompt = $$"""
            Based on the following task description, suggest the best nodes to use:

            TASK: {{request.TaskDescription}}

            Provide:
            1. A list of recommended nodes with their nodeTypes
            2. A brief explanation of why each node is needed
            3. The order they should be connected
            4. Any configuration tips

            If no existing nodes can accomplish the task, describe what new node would be needed.
            """;

        AssistantResponse response = await ChatAsync(new AssistantRequest { Message = prompt }, cancellationToken);

        return new NodeSuggestionResponse
        {
            Success = response.Success,
            Suggestions = response.Message,
            Error = response.Error,
        };
    }

    /// <summary>
    /// Help configure a node's inputs.
    /// </summary>
    public async Task<NodeConfigurationResponse> ConfigureNodeAsync(NodeConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        string prompt = $$"""
            Help me configure the following node:

            NODE TYPE: {{request.NodeType}}
            NODE ID: {{request.NodeId}}

            AVAILABLE OUTPUTS FROM PREVIOUS NODES:
            {{JsonSerializer.Serialize(request.AvailableOutputs, JsonOptions)}}

            USER INTENT: {{request.Intent}}

            Provide the complete inputs configuration as JSON that I can use directly.
            Use the template syntax for references to other nodes.
            """;

        AssistantResponse response = await ChatAsync(new AssistantRequest { Message = prompt, WorkflowContext = request.WorkflowContext }, cancellationToken);

        return new NodeConfigurationResponse
        {
            Success = response.Success,
            Configuration = response.Message,
            Error = response.Error,
        };
    }

    /// <summary>
    /// Chat with streaming response — emits AG-UI-compatible chunks.
    /// Text is streamed word-by-word; widget tool calls are emitted as dedicated chunks.
    /// </summary>
    public async IAsyncEnumerable<AssistantStreamChunk> ChatStreamAsync(AssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AppSettings appSettings = await _appSettingsService.GetAsync(cancellationToken);
        string credentialsId = appSettings.AIAssistant.CredentialsId;

        _logger.LogInformation("Processing streaming assistant request with credentials: {CredentialsId}", credentialsId);

        ILlmClient llmClient;
        string? factoryError = null;
        try
        {
            llmClient = await _llmClientFactory.CreateAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            factoryError = ex.Message;
            llmClient = null!;
        }

        if (factoryError is not null)
        {
            yield return new AssistantStreamChunk { Error = factoryError, Done = true };
            yield break;
        }

        // Build system prompt — include widget catalogue so the LLM knows what it can render
        string systemPrompt = _knowledgeBase.GetSystemPrompt();
        if (!string.IsNullOrWhiteSpace(request.WorkflowContext))
            systemPrompt += $"\n\n# CURRENT WORKFLOW CONTEXT\n```json\n{request.WorkflowContext}\n```";
        systemPrompt += "\n\n" + BuildWidgetSystemPromptSection();

        // Build conversation messages
        var messages = new List<LlmChatMessage> { new("system", systemPrompt) };
        if (request.History is { Count: > 0 })
        {
            foreach (var h in request.History)
                messages.Add(new LlmChatMessage(h.Role.ToLowerInvariant(), h.Content));
        }
        messages.Add(new LlmChatMessage("user", request.Message));

        // Build widget tool definitions
        var tools = BuildWidgetTools();
        var options = new LlmConversationOptions(Temperature: 0.3, MaxTokens: 2000);

        var result = await llmClient.CompleteConversationAsync(messages, tools, options, cancellationToken: cancellationToken);
        if (result.IsFailure || result.Value is null)
        {
            yield return new AssistantStreamChunk { Error = result.Error ?? "LLM completion failed.", Done = true };
            yield break;
        }

        var response = result.Value;
        _logger.LogInformation("Assistant response: {Tokens} tokens, {ToolCalls} tool call(s)", response.TokensUsed, response.ToolCalls.Count);

        // Stream text content word-by-word
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            foreach (string word in response.Content.Split(' '))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                yield return new AssistantStreamChunk { Content = word + " ", Done = false };
                await Task.Delay(15, cancellationToken);
            }
        }

        // Emit widget invocations derived from tool calls
        foreach (var toolCall in response.ToolCalls)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            string? widgetType = null;
            string? widgetData = null;
            try
            {
                using var doc = JsonDocument.Parse(toolCall.ArgumentsJson);
                widgetType = doc.RootElement.TryGetProperty("widget_type", out var wt) ? wt.GetString() : toolCall.Name;
                widgetData = doc.RootElement.TryGetProperty("data", out var wd) ? wd.GetRawText() : toolCall.ArgumentsJson;
            }
            catch
            {
                widgetType = toolCall.Name;
                widgetData = toolCall.ArgumentsJson;
            }

            yield return new AssistantStreamChunk
            {
                Done = false,
                WidgetName = widgetType,
                WidgetArgumentsJson = widgetData,
            };
        }

        yield return new AssistantStreamChunk { Done = true, TokensUsed = response.TokensUsed };
    }

    // ── Widget helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a single <c>render_widget</c> tool definition covering all widget types
    /// registered in the bundle registry. Returns <c>null</c> if no bundles are registered.
    /// </summary>
    private IReadOnlyList<JsonObject>? BuildWidgetTools()
    {
        var bundles = _bundleRegistry.GetAll();
        if (bundles.Count == 0)
            return null;

        var widgetTypes = bundles.SelectMany(b => b.Widgets).Select(w => w.TypeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (widgetTypes.Count == 0)
            return null;

        var enumValues = JsonSerializer.Serialize(widgetTypes, WebJson);
        var schema = $$"""
            {
              "type": "object",
              "properties": {
                "widget_type": {
                  "type": "string",
                  "enum": {{enumValues}},
                  "description": "The widget component to render"
                },
                "title": {
                  "type": "string",
                  "description": "Optional display title shown above the widget"
                },
                "data": {
                  "type": "object",
                  "description": "Widget data payload — structure depends on widget_type (see system prompt for examples)"
                }
              },
              "required": ["widget_type", "data"]
            }
            """;

        var tool = LlmToolDefinitionJson.Create(
            "render_widget",
            "Render an interactive visual widget inline in the chat. Use instead of plain text when visual output (chart, table, code block, status card, etc.) is clearer or more useful. You may call this multiple times in a single response.",
            schema
        );

        return [tool];
    }

    /// <summary>
    /// Returns the system prompt section that teaches the LLM about available widgets.
    /// </summary>
    private string BuildWidgetSystemPromptSection()
    {
        var bundles = _bundleRegistry.GetAll();
        if (bundles.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# INTERACTIVE WIDGETS");
        sb.AppendLine("You can render interactive visual widgets in your responses by calling the `render_widget` tool.");
        sb.AppendLine("Use widgets when visual output is clearer or more useful than plain text.");
        sb.AppendLine("You may call `render_widget` multiple times per response. Always add a brief text explanation alongside widgets.");
        sb.AppendLine();
        sb.AppendLine("## Available widget types");

        foreach (var bundle in bundles)
        {
            sb.AppendLine($"\n### {bundle.DisplayName}");
            foreach (var widget in bundle.Widgets)
                sb.AppendLine($"- **{widget.TypeKey}** ({widget.Category}): {widget.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("## Widget data shapes (examples)");
        sb.AppendLine("- **BarChart / LineChart / AreaChart**: `{ \"labels\": [\"Q1\",\"Q2\",\"Q3\"], \"datasets\": [{ \"label\": \"Revenue\", \"data\": [120, 145, 98] }] }`");
        sb.AppendLine("- **PieChart / RadarChart**: `{ \"labels\": [\"A\",\"B\",\"C\"], \"datasets\": [{ \"data\": [30, 50, 20] }] }`");
        sb.AppendLine("- **MetricCard**: `{ \"label\": \"Total runs\", \"value\": \"1,204\", \"unit\": \"runs\", \"trend\": \"+12%\" }`");
        sb.AppendLine("- **StatGrid**: `{ \"items\": [{ \"label\": \"Agents\", \"value\": \"8\" }, { \"label\": \"Workflows\", \"value\": \"24\" }] }`");
        sb.AppendLine("- **KeyValue**: `{ \"items\": [{ \"key\": \"Status\", \"value\": \"Active\" }, { \"key\": \"Version\", \"value\": \"2.1\" }] }`");
        sb.AppendLine("- **CodeBlock**: `{ \"language\": \"python\", \"code\": \"print('hello')\" }`");
        sb.AppendLine("- **JsonViewer**: `{ \"json\": { \"any\": \"object\" } }`");
        sb.AppendLine("- **StatusCard**: `{ \"status\": \"success\", \"title\": \"Deployment complete\", \"message\": \"All services are healthy.\" }`");
        sb.AppendLine("- **Timeline**: `{ \"events\": [{ \"date\": \"2025-01-01\", \"title\": \"Launch\", \"description\": \"v1.0 released\" }] }`");
        sb.AppendLine("- **Latex**: `{ \"expression\": \"\\\\frac{1}{2}mv^2\" }`");
        sb.AppendLine("- **Svg**: `{ \"content\": \"<svg>...</svg>\" }`");

        return sb.ToString();
    }
}

#region Request/Response Models

public sealed record AssistantRequest
{
    public required string Message { get; init; }
    public string? WorkflowContext { get; init; }
    public List<ConversationMessage>? History { get; init; }
}

public sealed record ConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed record AssistantResponse
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public int TokensUsed { get; init; }
}

public sealed record NodeSuggestionRequest
{
    public required string TaskDescription { get; init; }
}

public sealed record NodeSuggestionResponse
{
    public bool Success { get; init; }
    public string? Suggestions { get; init; }
    public string? Error { get; init; }
}

public sealed record NodeConfigurationRequest
{
    public required string CredentialsId { get; init; }
    public required string NodeType { get; init; }
    public required string NodeId { get; init; }
    public required string Intent { get; init; }
    public Dictionary<string, List<string>>? AvailableOutputs { get; init; }
    public string? WorkflowContext { get; init; }
}

public sealed record NodeConfigurationResponse
{
    public bool Success { get; init; }
    public string? Configuration { get; init; }
    public string? Error { get; init; }
}

public sealed record AssistantStreamChunk
{
    public string? Content { get; init; }
    public bool Done { get; init; }
    public string? Error { get; init; }
    public int TokensUsed { get; init; }

    /// <summary>When set, this chunk represents a widget invocation rather than text content.</summary>
    public string? WidgetName { get; init; }

    /// <summary>JSON string of the widget data arguments. Valid when <see cref="WidgetName"/> is set.</summary>
    public string? WidgetArgumentsJson { get; init; }
}

#endregion
