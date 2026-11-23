using ApiAggregator.Engine.Execution;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ApiAggregator.Gateway;

/// <summary>
/// Retries, timeouts and circuit breakers for downstream calls (ADR 0017). The benchmark (docs/guides/benchmarking.md)
/// showed what the defaults did with one broken service: a single shared circuit broke calls to every service, its
/// "rejected" exceptions became 500s, and retries waited about 2 s, as long as a service's whole timeout.
/// </summary>
public static class DownstreamResilience
{
    public static void AddDownstreamResilience(this IServiceCollection services, IConfiguration configuration)
    {
        // A bulkhead: at most this many calls to one service at once. A slow or misbehaving service can then hold only
        // part of the gateway; calls beyond it fail at once (502 unreachable) instead of piling up.
        var callsPerService = configuration.GetValue("Gateway:MaxCallsInFlightPerService", 200);

        services.AddHttpClient(EngineHttpClients.Downstream)
            // Outside the pipeline, so whatever it rejects (open circuit, its own timeout, too many calls) reaches the
            // engine as a connection problem: an optional step is skipped, a required one gives 502. Never a 500.
            .AddHttpMessageHandler(() => new ResilienceRejectionHandler())
            .AddStandardResilienceHandler(o =>
            {
                // Retry only safe, idempotent calls (GET/HEAD/OPTIONS...), never POST/PUT/PATCH/DELETE. Quickly: the
                // step's own timeout (often 2–5 s) bounds the whole call, so a 2 s back-off would never get to retry.
                o.Retry.DisableForUnsafeHttpMethods();
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.FromMilliseconds(100);

                // The engine applies each step's timeout (at most 60 s). These only stop the pipeline cutting it shorter.
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120); // must be at least twice the attempt timeout

                o.RateLimiter.DefaultRateLimiterOptions.PermitLimit = callsPerService;
                o.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 0;
            })
            // One circuit per service (the engine names it on every request), not per host: several services often share one.
            .SelectPipelineBy(_ => request =>
                request.Options.TryGetValue(EngineHttpClients.ServiceId, out var service) ? service : request.RequestUri?.Authority ?? "unknown");
    }
}

/// <summary>Turns a resilience-pipeline rejection into the HttpRequestException the engine treats as "couldn't reach the service".</summary>
public sealed class ResilienceRejectionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (ExecutionRejectedException rejected)
        {
            // The type says which (BrokenCircuitException, TimeoutRejectedException...); never the request's values.
            throw new HttpRequestException(HttpRequestError.Unknown, $"The call wasn't made: {rejected.GetType().Name}.", rejected);
        }
    }
}
