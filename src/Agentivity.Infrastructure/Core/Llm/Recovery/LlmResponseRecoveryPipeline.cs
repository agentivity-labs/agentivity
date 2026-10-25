using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm.Recovery;

/// <summary>
/// Orchestrates response validation and recovery.
/// On an invalid response the pipeline walks the registered handlers in order,
/// honouring a hard retry cap to prevent infinite loops.
/// </summary>
public sealed class LlmResponseRecoveryPipeline
{
    private readonly int _maxRetries;
    private readonly ILlmResponseValidator _validator;
    private readonly IReadOnlyList<ILlmResponseRecoveryHandler> _handlers;
    private readonly ILogger _logger;

    public LlmResponseRecoveryPipeline(ILlmResponseValidator validator, IReadOnlyList<ILlmResponseRecoveryHandler> handlers, ILogger logger, int maxRetries = 2)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRetries = maxRetries > 0 ? maxRetries : 2;
    }

    /// <summary>
    /// Executes <paramref name="executeFunc"/> and, if the response is invalid,
    /// runs recovery handlers to repair or retry the call.
    /// </summary>
    public async Task<OperationResult<LlmConversationResponse>> ExecuteWithRecoveryAsync(
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions?, CancellationToken, Task<OperationResult<LlmConversationResponse>>> executeFunc,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<JsonObject>? tools,
        LlmConversationOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(executeFunc);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        var result = await executeFunc(messages, tools, options, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return result;

        if (_validator.IsValid(result.Value!))
            return result;

        _logger.LogDebug("LLM response invalid (Content empty, no tool calls). Running recovery handlers.");

        var retriesUsed = 0;
        foreach (var handler in _handlers)
        {
            var context = new LlmResponseRecoveryContext(messages, tools, options, result.Value!);
            var decision = await handler.TryRecoverAsync(context, cancellationToken).ConfigureAwait(false);

            switch (decision)
            {
                case LlmResponseRecoveryDecision.Handled handled:
                    _logger.LogDebug("Recovery handler '{Handler}' produced a repaired response.", handler.Name);
                    return OperationResult<LlmConversationResponse>.Success(handled.RepairedResponse);

                case LlmResponseRecoveryDecision.Retry retry when retriesUsed < _maxRetries:
                    retriesUsed++;
                    var retryMessages = retry.ModifiedMessages ?? messages;
                    var retryOptions = retry.ModifiedOptions ?? options;

                    _logger.LogDebug("Recovery handler '{Handler}' requested retry {Attempt}/{Max}.", handler.Name, retriesUsed, _maxRetries);

                    result = await executeFunc(retryMessages, tools, retryOptions, cancellationToken).ConfigureAwait(false);

                    if (result.IsSuccess && _validator.IsValid(result.Value!))
                    {
                        _logger.LogDebug("Retry from handler '{Handler}' produced a valid response.", handler.Name);
                        return result;
                    }

                    break;

                case LlmResponseRecoveryDecision.Retry:
                    _logger.LogDebug("Recovery handler '{Handler}' requested retry but retry budget exhausted ({Max}).", handler.Name, _maxRetries);
                    break;

                case LlmResponseRecoveryDecision.Skip skip:
                    _logger.LogDebug("Recovery handler '{Handler}' skipped: {Reason}", handler.Name, skip.Reason);
                    break;
            }
        }

        _logger.LogDebug("All recovery handlers exhausted. Returning last response as-is.");
        return result;
    }
}
