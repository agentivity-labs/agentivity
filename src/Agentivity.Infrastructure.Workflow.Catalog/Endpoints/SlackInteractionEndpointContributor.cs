using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Plugins;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Endpoints;

public sealed class SlackInteractionEndpointContributor : IEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        _ = endpoints
            .MapV1()
            .MapPost(
                "/interactions/slack/events",
                async (HttpRequest httpRequest, InteractionStore store, WorkflowEngine engine, CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, CancellationToken ct) =>
                {
                    using var reader = new StreamReader(httpRequest.Body);
                    string rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(rawBody))
                    {
                        return Results.BadRequest(new { status = "failed", error = "Slack request body is empty." });
                    }

                    var processor = new SlackInteractionWebhookProcessor(store, engine, credentialsService, httpClientFactory, loggerFactory.CreateLogger<SlackInteractionWebhookProcessor>());

                    SlackWebhookProcessResult result = await processor.ProcessAsync(httpRequest.Headers["X-Slack-Request-Timestamp"].ToString(), httpRequest.Headers["X-Slack-Signature"].ToString(), rawBody, ct).ConfigureAwait(false);

                    return result.Outcome switch
                    {
                        SlackWebhookProcessOutcome.Challenge => Results.Text(result.ChallengeText ?? string.Empty, "text/plain"),
                        SlackWebhookProcessOutcome.Unauthorized => Results.Unauthorized(),
                        SlackWebhookProcessOutcome.Ignored => Results.Ok(new { status = "ignored", reason = result.Reason }),
                        SlackWebhookProcessOutcome.Failed => Results.BadRequest(new { status = "failed", error = result.Error }),
                        SlackWebhookProcessOutcome.Resumable when result.SuspendedContext is not null => ResumeSlackInteraction(result, engine),
                        _ => Results.BadRequest(new { status = "failed", error = "Slack interaction could not be processed." }),
                    };
                }
            )
            .WithSummary("Receive a Slack reply and resume the waiting run");
    }

    private static IResult ResumeSlackInteraction(SlackWebhookProcessResult result, WorkflowEngine engine)
    {
        _ = engine.ResumeAfterInteractionAsync(result.SuspendedContext!, CancellationToken.None);
        return Results.Ok(
            new
            {
                status = "resumed",
                runId = result.RunId,
                requestId = result.RequestId,
            }
        );
    }
}
