using System.Collections.Concurrent;
using Ago.Chat.Domain;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Modules;

/// <summary>
/// `20-07`: the direct analogue of <c>Ago.Chat.Module.Channels.ChannelResiliencePipelines</c>, keyed by
/// <see cref="ModuleKey"/> instead of <see cref="ChannelKind"/> - `resilience.md`'s own per-channel
/// keying reasoning applies unchanged: one module's outage (a module HTTP boundary this deployment does
/// not control, per that document's boundary table) must not open a breaker shared with a second
/// module's calls, and every call for one module shares the same pipeline instance so the breaker's
/// state and the bulkhead's in-flight count actually mean something across calls.
///
/// <para>Registered as a singleton (<c>ChatModule</c>), for the identical reason
/// <c>ChannelResiliencePipelines</c>' own remarks give: a scoped or transient lifetime would silently
/// rebuild a fresh, un-tripped breaker per DI scope.</para>
/// </summary>
public sealed class ModuleResiliencePipelines(ResiliencePipelineOptions options)
{
    public const string PipelineName = "Modules";

    private readonly ConcurrentDictionary<ModuleKey, ResiliencePipeline> _byModule = new();

    public ResiliencePipeline For(ModuleKey key) => _byModule.GetOrAdd(key, _ => Build(options));

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

        if (options.Bulkhead is { } bulkhead)
        {
            builder.WithBulkhead(bulkhead);
        }

        if (options.CircuitBreaker is { } breaker)
        {
            builder.WithCircuitBreaker(breaker, IsBreakerWorthy);
        }

        if (options.Retry is { } retry)
        {
            builder.WithRetry(retry, IsRetryWorthy);
        }

        if (options.Timeout is { } timeout)
        {
            builder.WithTimeout(timeout);
        }

        return builder.Build();
    }

    /// <summary>Cancellation is never the module's fault - `ChannelResiliencePipelines`' own
    /// convention, reused unchanged.</summary>
    private static bool IsBreakerWorthy(Exception ex) => ex is not OperationCanceledException;

    /// <summary>
    /// Same exclusion. Unlike the channel boundary, <c>IModuleGateway</c> has no terminal-refusal
    /// return-value case at all (that interface's own remarks) - every exception this predicate ever
    /// sees, including a <see cref="Application.Abstractions.ModuleUnreachableException"/> raised for a
    /// non-2xx response, is a candidate for retry. That is safe for the same reason
    /// <c>ChannelResiliencePipelines</c>' own remarks give for the channel boundary: a start-task call
    /// carries its own <c>chatTaskId</c> as an idempotency key the module is expected to recognise on a
    /// retried delivery, and a reply carries the same. Retrying a call the module has already fully
    /// processed once is therefore no worse than an ordinary at-least-once redelivery elsewhere in this
    /// system - not free of cost (a module might process a trigger twice if its own idempotency check is
    /// itself imperfect), but never a correctness violation on Chat's own side.
    /// </summary>
    private static bool IsRetryWorthy(Exception ex) => ex is not OperationCanceledException;
}
