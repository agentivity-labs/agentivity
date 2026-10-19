using System.Net;
using System.Net.Http.Headers;
using Agentivity.Infrastructure.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Web;

public enum WebRateLimitProfile
{
    None,
    Fast,
    Medium,
    Slow,
}

public sealed record WebRateLimitPolicy(int MaxRetries, TimeSpan BaseDelay, TimeSpan MaxDelay);

public sealed class WebRequestDebugException : InvalidOperationException
{
    public WebRequestDebugException(string message, DebugTraceItem debugTrace)
        : base(message)
    {
        DebugTrace = debugTrace ?? throw new ArgumentNullException(nameof(debugTrace));
    }

    public DebugTraceItem DebugTrace { get; }
}

public sealed record WebRateLimitSendResult(HttpResponseMessage Response, DebugTraceItem DebugTrace);

public static class WebRateLimiting
{
    public static WebRateLimitProfile ParseProfile(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => WebRateLimitProfile.None,
            "fast" => WebRateLimitProfile.Fast,
            "slow" => WebRateLimitProfile.Slow,
            _ => WebRateLimitProfile.Medium,
        };

    public static WebRateLimitPolicy GetPolicy(WebRateLimitProfile profile) =>
        profile switch
        {
            WebRateLimitProfile.None => new WebRateLimitPolicy(0, TimeSpan.Zero, TimeSpan.Zero),
            WebRateLimitProfile.Fast => new WebRateLimitPolicy(2, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2)),
            WebRateLimitProfile.Slow => new WebRateLimitPolicy(6, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)),
            _ => new WebRateLimitPolicy(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15)),
        };

    public static async Task<WebRateLimitSendResult> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        WebRateLimitProfile profile,
        ILogger logger,
        string? kind = null,
        string? provider = null,
        CancellationToken cancellationToken = default,
        WebRateLimitPolicy? policyOverride = null
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var policy = policyOverride ?? GetPolicy(profile);
        var retryDelayMs = new List<double>();
        var usedRetryAfter = false;

        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();

            try
            {
                var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode) || attempt >= policy.MaxRetries)
                {
                    return new WebRateLimitSendResult(response, BuildDebugTrace(kind, provider, profile, attempt + 1, retryDelayMs, usedRetryAfter, (int)response.StatusCode, null));
                }

                var (delay, delayCameFromRetryAfter) = GetDelay(response.Headers.RetryAfter, attempt, policy);
                usedRetryAfter |= delayCameFromRetryAfter;
                retryDelayMs.Add(delay.TotalMilliseconds);
                logger.LogWarning("HTTP request hit transient status {StatusCode}. Retrying in {DelayMs} ms (attempt {Attempt}/{MaxAttempts}).", (int)response.StatusCode, delay.TotalMilliseconds, attempt + 2, policy.MaxRetries + 1);

                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            // A call refused for leading inside the network is not tried again: it would be refused again.
            catch (HttpRequestException ex) when (attempt < policy.MaxRetries && !PublicNetwork.IsRefusal(ex))
            {
                var (delay, _) = GetDelay(retryAfter: null, attempt, policy);
                retryDelayMs.Add(delay.TotalMilliseconds);
                logger.LogWarning(ex, "HTTP request failed with a transient transport error. Retrying in {DelayMs} ms (attempt {Attempt}/{MaxAttempts}).", delay.TotalMilliseconds, attempt + 2, policy.MaxRetries + 1);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (PublicNetwork.IsRefusal(ex))
            {
                // Said as it is, so whoever called knows the address was refused, not unreachable.
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new WebRequestDebugException(ex.Message, BuildDebugTrace(kind, provider, profile, attempt + 1, retryDelayMs, usedRetryAfter, null, ex.Message));
            }
        }
    }

    public static bool ShouldRetry(HttpStatusCode statusCode) => statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static (TimeSpan Delay, bool UsedRetryAfter) GetDelay(RetryConditionHeaderValue? retryAfter, int attempt, WebRateLimitPolicy policy)
    {
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return (delta <= policy.MaxDelay || policy.MaxDelay == TimeSpan.Zero ? delta : policy.MaxDelay, true);
        }

        if (retryAfter?.Date is DateTimeOffset retryDate)
        {
            var delay = retryDate - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return (delay <= policy.MaxDelay || policy.MaxDelay == TimeSpan.Zero ? delay : policy.MaxDelay, true);
            }
        }

        if (policy.BaseDelay == TimeSpan.Zero)
        {
            return (TimeSpan.Zero, false);
        }

        var exponentialDelay = TimeSpan.FromMilliseconds(policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return (exponentialDelay <= policy.MaxDelay ? exponentialDelay : policy.MaxDelay, false);
    }

    private static DebugTraceItem BuildDebugTrace(string? kind, string? provider, WebRateLimitProfile profile, int attemptCount, List<double> retryDelayMs, bool usedRetryAfter, int? finalStatusCode, string? finalError)
    {
        var requestLabel = BuildRequestLabel(kind, provider);
        var children = new List<DebugTraceItem>();
        var fields = new List<DebugTraceField> { new("Rate limit profile", profile.ToString().ToLowerInvariant()), new("Attempt count", attemptCount.ToString()), new("Retry count", Math.Max(0, attemptCount - 1).ToString()), new("Total wait ms", retryDelayMs.Sum(delay => delay).ToString("0")) };

        if (finalStatusCode.HasValue)
        {
            fields.Add(new DebugTraceField("Final status code", finalStatusCode.Value.ToString()));
        }

        for (var index = 0; index < attemptCount; index++)
        {
            if (index < attemptCount - 1)
            {
                var delayMs = index < retryDelayMs.Count ? retryDelayMs[index] : 0;
                children.Add(new DebugTraceItem(Kind: "attempt", Name: $"Attempt {index + 1}", Message: "The request did not succeed and triggered a retry.", Status: DebugTraceStatus.Warning));
                children.Add(new DebugTraceItem(Kind: "wait", Name: $"Retry wait {index + 1}", Message: $"Waiting {delayMs:0} ms before retry {index + 2}.", Status: DebugTraceStatus.Info));
            }
            else if (finalStatusCode.HasValue)
            {
                children.Add(new DebugTraceItem(Kind: "attempt", Name: $"Attempt {index + 1}", Message: $"The request completed with HTTP {finalStatusCode.Value}.", Status: finalStatusCode.Value >= 400 ? DebugTraceStatus.Error : DebugTraceStatus.Info));
            }
            else if (!string.IsNullOrWhiteSpace(finalError))
            {
                children.Add(new DebugTraceItem(Kind: "attempt", Name: $"Attempt {index + 1}", Message: $"The request failed: {finalError}", Status: DebugTraceStatus.Error));
            }
        }

        if (attemptCount > 1)
        {
            children.Add(new DebugTraceItem(Kind: "warning", Name: "Retries", Message: $"The request required {attemptCount - 1} retry(s) before completion.", Status: DebugTraceStatus.Warning));
        }

        if (usedRetryAfter)
        {
            children.Add(new DebugTraceItem(Kind: "warning", Name: "Retry-After", Message: "The remote service asked the client to wait before retrying.", Status: DebugTraceStatus.Warning));
        }

        var summary = finalStatusCode is >= 200 and < 400
            ? attemptCount == 1
                ? $"{requestLabel} succeeded on the first attempt."
                : $"{requestLabel} succeeded after {attemptCount} attempts."
            : finalStatusCode.HasValue
                ? $"{requestLabel} failed with HTTP {finalStatusCode.Value} after {attemptCount} attempts."
                : $"{requestLabel} failed after {attemptCount} attempts.";

        return new DebugTraceItem(
            Kind: kind ?? "request",
            Name: requestLabel,
            Message: summary,
            Status: !string.IsNullOrWhiteSpace(finalError) || finalStatusCode is >= 400 ? DebugTraceStatus.Error
                : attemptCount > 1 || usedRetryAfter ? DebugTraceStatus.Warning
                : DebugTraceStatus.Info,
            Fields: fields,
            Children: children.Count == 0 ? null : children
        );
    }

    private static string BuildRequestLabel(string? kind, string? provider)
    {
        var requestType = kind switch
        {
            "web_search" => "Web search request",
            "web_scrape" => "Web scraping request",
            _ => "Request",
        };

        return string.IsNullOrWhiteSpace(provider) ? requestType : $"{requestType} ({provider})";
    }
}
