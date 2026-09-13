using System.Collections.Concurrent;
using Ago.Chat.Domain;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Modules;

/// <summary>
/// `22-31`: a distinct, per-module-keyed resilience pipeline for exactly one call -
/// <see cref="Application.Abstractions.IModuleRegistrationGateway.ExportTenantDataAsync"/> - not
/// <see cref="ModuleResiliencePipelines"/> (the module-task boundary's own pipeline, sized for a single
/// visitor message) and not <c>Infrastructure.Modules.HttpModuleRegistrationGateway</c>'s usual
/// "deliberately unwrapped" treatment of every other method on that gateway either. Both existing
/// choices are wrong for this one call for the same underlying reason, stated in the backlog item's own
/// Answered section: this is the one provisioning-channel call that now has to carry "a whole tenant's
/// calendar history" rather than a small operator-issued command, so it earns its own budget rather than
/// borrowing either neighbour's.
///
/// <para><b>Numbers, and why - starting points, not measurements</b> (the identical honesty
/// <see cref="ModuleResiliencePipelines"/>'s own <c>ChatModule.ConfigureModuleResilienceDefaults</c>
/// states for its sibling pipeline). <b>Timeout: 2 minutes per attempt</b> - generous next to the 5
/// seconds a visitor is made to wait on <see cref="ModuleResiliencePipelines"/>'s own boundary, because
/// nobody is watching a spinner for this one (`SiteExportJobOptions.Interval`'s own remarks: this job is
/// polled for completion, never awaited synchronously by a person) and a real tenant's calendar history
/// can genuinely take longer than an operator command to transfer - but still bounded, because a
/// tenant's whole export must still fail loud on a genuinely stuck module rather than hang a sweep cycle
/// indefinitely. <b>Retry: two attempts, one-second exponential backoff</b> - a `GET` is naturally safe
/// to repeat (nothing on this call writes anything), so a transient blip is worth one clean retry the
/// same way <see cref="ModuleResiliencePipelines"/>'s own module-task boundary already retries its own
/// calls; the backoff starts slower than that boundary's 200ms because a repeated multi-second-to-minute
/// transfer is a heavier retry to repeat than a small JSON body's own.</para>
///
/// <para><b>No circuit breaker, no bulkhead - unlike <see cref="ModuleResiliencePipelines"/>.</b> This
/// call is rare by construction (<c>SiteExportJobOptions.BatchSize</c> bounds how many run concurrently
/// at all, and an export is a tenant- or operator-initiated action, not steady traffic), the identical
/// "a circuit breaker keyed here would trip on legitimate sequential retries across a handful of
/// tenants rather than on real traffic volume" reasoning
/// <c>HttpModuleRegistrationGateway</c>'s own remarks already give for leaving the rest of that gateway
/// entirely unwrapped. Timeout and retry alone are enough to keep one stuck module from stalling a
/// sweep forever, without the added state a breaker or bulkhead would carry for a call this
/// infrequent.</para>
/// </summary>
public sealed class ModuleExportResiliencePipelines(ResiliencePipelineOptions options)
{
    public const string PipelineName = "ModuleExport";

    private readonly ConcurrentDictionary<ModuleKey, ResiliencePipeline> _byModule = new();

    public ResiliencePipeline For(ModuleKey key) => _byModule.GetOrAdd(key, _ => Build(options));

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

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

    /// <summary>Cancellation is never the module's fault - the identical exclusion
    /// <see cref="ModuleResiliencePipelines"/>'s own predicate applies.</summary>
    private static bool IsRetryWorthy(Exception ex) => ex is not OperationCanceledException;
}
