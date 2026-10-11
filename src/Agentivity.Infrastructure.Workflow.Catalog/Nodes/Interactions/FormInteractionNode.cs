using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Interactions;

/// <summary>
/// Structured form interaction node for web-style channels that collect explicit field values.
/// Suspends workflow until the user submits the requested form data.
/// </summary>
public sealed class FormInteractionNode : BaseInteractionNode
{
    public FormInteractionNode(InteractionStore store, ILogger<FormInteractionNode> logger)
        : base("interaction.form", store, logger)
    {
        DefineMetadata("Form Interaction", "Collects structured form input through the connected channel", "interaction");
        DefineEffects(NodeEffects.SendsMessage);

        DefineInput("title", ArgumentType.Text, "Title shown to the user", required: false);
        DefineInput("description", ArgumentType.Text, "Description/instructions", ui: new ArgumentUiMetadata { Multiline = true, Rows = 4 }, required: false);
        DefineInput(
            "fields",
            ArgumentType.Text,
            "JSON array of field definitions",
            ui: new ArgumentUiMetadata
            {
                Multiline = true,
                Rows = 12,
                Format = "json",
            },
            required: true
        );

        DefineOutput("response_data", ArgumentType.Text, "User response as JSON");
        DefineOutput("responded_by", ArgumentType.Text, "Who responded");
        DefineOutput("outcome", ArgumentType.Text, "Response outcome");

        DefineIcon(NodeIcon.Material(0xe8d2, "#14b8a6"));
    }

    protected override InteractionRequest BuildRequest(NodeConfig config, WorkflowRunServices services, ChannelBinding channel)
    {
        ArgumentInputValues args = GetInputs(config);
        int timeout = channel.Properties.GetInt("timeout_minutes") ?? 60;

        return new InteractionRequest
        {
            Id = Guid.NewGuid(),
            RunId = services.RunId,
            NodeId = config.Node.Id,
            Kind = InteractionKind.Input,
            ChannelType = channel.ChannelType,
            Title = args.GetText("title") ?? "Input required",
            Description = args.GetText("description"),
            Respondent = channel.Respondent,
            Fields = ParseFields(args),
            Timeout = timeout > 0 ? TimeSpan.FromMinutes(timeout) : null,
            // Same fix as HumanInteractionNode.BuildRequest: these were never set at all here, so
            // a chat-backed channel (ai.interactions.chat) had no ExecutionId to find this
            // execution's thread by, and no StreamId to route its published events to.
            ExecutionId = services.ExecutionId,
            StreamId = services.StreamId,
        };
    }

    protected override NodeResult MapResponse(InteractionResponse response)
    {
        // Prefer structured Data; fall back to parsing ResponseText as JSON
        // (the Flutter client sends field values as a JSON-encoded string in ResponseText).
        var data = response.Data;
        if (data is null && !string.IsNullOrWhiteSpace(response.ResponseText))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(response.ResponseText, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (parsed is not null)
                    data = parsed;
            }
            catch (JsonException)
            { /* free-text response — not JSON */
            }
        }

        var result = NodeResult.Create().AddText("response_data", data is not null ? JsonSerializer.Serialize(data) : response.ResponseText ?? string.Empty).AddText("responded_by", response.RespondedBy ?? string.Empty).AddText("outcome", response.Outcome.ToString());

        // Expose each field value as an individual output so templates can use {{field_name}} directly.
        if (data is not null)
            foreach (var kvp in data)
                result = result.AddText(kvp.Key, kvp.Value?.ToString() ?? string.Empty);

        return result;
    }

    private static IReadOnlyList<InteractionField> ParseFields(ArgumentInputValues args)
    {
        JsonElement? element = args.GetJsonElement("fields");
        if (element is not { ValueKind: JsonValueKind.Array } jsonArray)
        {
            return [];
        }

        try
        {
            return jsonArray.Deserialize<List<InteractionField>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
