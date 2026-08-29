using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.Modules;

/// <summary>
/// `20-07`: wraps <see cref="IModuleGateway"/> in its module's own <see cref="ModuleResiliencePipelines"/>
/// pipeline - the direct analogue of <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>,
/// composition over inheritance for the identical reason that type's own remarks give: the decision to
/// protect this boundary lives at the composition root (<c>ChatModule</c>), not inside
/// <c>HttpModuleGateway</c>, which stays a plain "call the provider, translate the answer" class that is
/// trivial to unit-test with no pipeline at all.
/// </summary>
public sealed class ResilientModuleGateway(
    IModuleGateway inner, ModuleResiliencePipelines pipelines) : IModuleGateway
{
    public async Task<StartModuleTaskResult> StartTaskAsync(
        EnabledModuleEndpoint module, StartModuleTaskRequest request, CancellationToken cancellationToken)
    {
        var pipeline = pipelines.For(module.ModuleKey);
        return await pipeline.ExecuteAsync(
            async token => await inner.StartTaskAsync(module, request, token), cancellationToken);
    }

    public async Task<SubmitModuleReplyResult> SubmitReplyAsync(
        EnabledModuleEndpoint module, SubmitModuleReplyRequest request, CancellationToken cancellationToken)
    {
        var pipeline = pipelines.For(module.ModuleKey);
        return await pipeline.ExecuteAsync(
            async token => await inner.SubmitReplyAsync(module, request, token), cancellationToken);
    }
}
