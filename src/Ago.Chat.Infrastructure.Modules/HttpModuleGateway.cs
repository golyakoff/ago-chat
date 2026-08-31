using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `20-07`: the plain "call the provider, translate the answer" implementation of
/// <see cref="IModuleGateway"/> - written as if the module always answers, exactly like
/// <c>MaxChannelAdapter</c>/<c>TelegramChannelAdapter</c> are written for their own providers.
/// Resilience (timeout, retry, circuit breaker, bulkhead) is applied by wrapping this class
/// (<c>Ago.Chat.Module.Modules.ResilientModuleGateway</c>), never inside it - the same split
/// <see cref="IInboundChannelAdapter"/>'s own remarks describe.
///
/// <para><b>No fixed <c>BaseAddress</c>.</b> Unlike MAX or Telegram (one bot token, one fixed base URL
/// per credential), a module's entry point is a per-site, per-module value read from the registry at
/// call time (<see cref="EnabledModuleEndpoint.EntryPoint"/>) - so the typed <c>HttpClient</c> this
/// class is registered with (<c>ChatModule</c>) carries no base address, and every call builds its own
/// absolute URI from the endpoint it was handed.</para>
///
/// <para><b>Every failure becomes <see cref="ModuleUnreachableException"/>, at this one boundary.</b>
/// A thrown <see cref="HttpRequestException"/>/<see cref="TaskCanceledException"/> (connection refused,
/// DNS failure, timeout), a non-2xx status, and a response this project cannot parse as the wire
/// contract's own shape (including a step whose <c>kind</c>/<c>payload</c>/<c>actions</c> fail
/// <see cref="Domain.MessageContentKind"/>/<see cref="Domain.MessagePayload"/>/<see cref="Domain.MessageAction"/>'s
/// own validation) are all translated here - see <see cref="IModuleGateway"/>'s own remarks on why the
/// contract treats all three identically.</para>
/// </summary>
public sealed class HttpModuleGateway(HttpClient httpClient) : IModuleGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StartModuleTaskResult> StartTaskAsync(
        EnabledModuleEndpoint module, StartModuleTaskRequest request, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, "api/v1/module-tasks");
        var wireRequest = new StartTaskWireRequest(
            request.ChatTaskId, request.SiteId.Value, request.ConversationId.Value, request.TriggerText);

        var wireResponse = await PostAsync<StartTaskWireRequest, StartTaskWireResponse>(
            module.ModuleKey, uri, wireRequest, cancellationToken);

        return new StartModuleTaskResult(
            wireResponse.ExternalTaskId, ToModuleStep(module.ModuleKey, wireResponse.Step), wireResponse.Complete);
    }

    public async Task<SubmitModuleReplyResult> SubmitReplyAsync(
        EnabledModuleEndpoint module, SubmitModuleReplyRequest request, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-tasks/{Uri.EscapeDataString(request.ExternalTaskId)}/replies");
        var wireRequest = new SubmitReplyWireRequest(
            request.ChatTaskId, request.Kind.Value, request.Value, request.PhoneVerifiedAt);

        var wireResponse = await PostAsync<SubmitReplyWireRequest, SubmitReplyWireResponse>(
            module.ModuleKey, uri, wireRequest, cancellationToken);

        return new SubmitModuleReplyResult(
            wireResponse.Step is { } step ? ToModuleStep(module.ModuleKey, step) : null, wireResponse.Complete);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        ModuleKey moduleKey, Uri uri, TRequest wireRequest, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(uri, wireRequest, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A caller-cancelled request (a host draining) surfaces as TaskCanceledException too - the
            // resilience layer wrapping this class is the one place that distinguishes that from a real
            // timeout (ResilientModuleGateway's own remarks, matching ChannelResiliencePipelines'
            // established split), so this translation happens unconditionally here and the caller above
            // this gateway never sees the raw exception type either way.
            throw new ModuleUnreachableException(moduleKey, ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ModuleUnreachableException(
                moduleKey, $"module answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        TResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new ModuleUnreachableException(moduleKey, $"module returned a malformed response: {ex.Message}", ex);
        }

        return parsed ?? throw new ModuleUnreachableException(moduleKey, "module returned an empty response body.");
    }

    private static ModuleStep ToModuleStep(ModuleKey moduleKey, StepWireDto step)
    {
        try
        {
            var kind = new MessageContentKind(step.Kind);
            var payload = step.Payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element
                ? new MessagePayload(element.GetRawText())
                : (MessagePayload?)null;
            var actions = (step.Actions ?? []).Select(a => new MessageAction(a.Label, a.Value)).ToList();
            return new ModuleStep(kind, payload, actions);
        }
        catch (ArgumentException ex)
        {
            // The module answered, but with a step this closed vocabulary cannot represent - treated
            // identically to an unreachable module (IModuleGateway's own remarks): Chat has nothing
            // sensible to show for a step it cannot validate, and the caller's escalation path is the
            // same one either way.
            throw new ModuleUnreachableException(moduleKey, $"module returned an invalid step: {ex.Message}", ex);
        }
    }

    private static Uri BuildUri(Uri entryPoint, string relativePath)
    {
        var baseString = entryPoint.ToString();
        if (!baseString.EndsWith('/'))
        {
            baseString += "/";
        }

        return new Uri(new Uri(baseString), relativePath);
    }
}
