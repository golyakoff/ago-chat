using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `22-11`: the plain "call the provider, translate the answer" implementation of
/// <see cref="IModuleRegistrationGateway"/> - written as if the module always answers, the identical
/// split <see cref="HttpModuleGateway"/>'s own remarks describe: no resilience pipeline lives inside
/// this class, and none wraps it either, unlike <see cref="HttpModuleGateway"/>/<see cref="ResilientModuleGateway"/>.
/// Provisioning is a rare, operator-initiated call, not a hot path shared with every visitor message,
/// so a circuit breaker keyed per module here would trip on an operator's own retries rather than on
/// real traffic volume - the identical judgement `Ago.Calendar.Provisioner`'s own remarks make for
/// staying off `Ago.Calendar.Module`'s registered pipelines.
///
/// <para><b>No fixed <c>BaseAddress</c></b>, the identical reason <see cref="HttpModuleGateway"/>'s own
/// remarks give: <see cref="ModuleRegistrationTarget.EntryPoint"/> is a per-site, per-module value
/// read at call time, not a per-credential fixed host.</para>
///
/// <para><b>The provisioning secret rides in <c>X-Ago-Module-Provisioning-Secret</c>, sent verbatim -
/// not signed.</b> See <see cref="IModuleProvisioningAuthenticator"/>'s own remarks (each module
/// product's own copy) for why this is a plain shared-secret header rather than
/// <c>ModuleCallCredential</c>'s HMAC-signed-assertion format: two different mechanisms for two
/// different threat models, not a variant of one format.</para>
/// </summary>
public sealed class HttpModuleRegistrationGateway(HttpClient httpClient) : IModuleRegistrationGateway
{
    private const string ProvisioningSecretHeaderName = "X-Ago-Module-Provisioning-Secret";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RegisterAsync(
        ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
        string displayName, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-registrations/{module.SiteId.Value}");
        await SendAsync(
            HttpMethod.Put, uri, new { credential = credential.Value, displayName }, module.ModuleKey,
            provisioningSecret, cancellationToken);
    }

    public async Task RotateAsync(
        ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-registrations/{module.SiteId.Value}/rotate");
        await SendAsync(HttpMethod.Post, uri, new { newCredential = newCredential.Value }, module.ModuleKey, provisioningSecret, cancellationToken);
    }

    public async Task RevokeAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-registrations/{module.SiteId.Value}");
        await SendAsync(HttpMethod.Delete, uri, body: null, module.ModuleKey, provisioningSecret, cancellationToken);
    }

    /// <summary>`22-30`: `DELETE .../module-registrations/{tenantId}/tenant-data` - a distinct route
    /// from <see cref="RevokeAsync"/>'s own `DELETE .../module-registrations/{tenantId}`, on purpose
    /// (see <c>Ago.Calendar.Api.ChatModule.ModuleRegistrationEndpoints.HandleEraseAsync</c>'s own
    /// remarks on the calendar side for why folding the two together would make one HTTP call mean two
    /// irreversible things).</summary>
    public async Task<TenantDataErasureResult> EraseTenantDataAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-registrations/{module.SiteId.Value}/tenant-data");
        var response = await SendAsync(HttpMethod.Delete, uri, body: null, module.ModuleKey, provisioningSecret, cancellationToken);

        try
        {
            var parsed = await response.Content.ReadFromJsonAsync<TenantErasureWireResponse>(JsonOptions, cancellationToken);
            return parsed is null
                ? throw new ModuleUnreachableException(module.ModuleKey, "module returned an empty erasure response.")
                : new TenantDataErasureResult(parsed.TenantExisted, parsed.Confirmed);
        }
        catch (JsonException ex)
        {
            throw new ModuleUnreachableException(module.ModuleKey, $"module returned a malformed erasure response: {ex.Message}", ex);
        }
    }

    public async Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        var uri = BuildUri(module.EntryPoint, $"api/v1/module-registrations/{module.SiteId.Value}");
        var response = await SendAsync(HttpMethod.Get, uri, body: null, module.ModuleKey, provisioningSecret, cancellationToken);

        try
        {
            var parsed = await response.Content.ReadFromJsonAsync<StatusWireResponse>(JsonOptions, cancellationToken);
            return parsed is null
                ? throw new ModuleUnreachableException(module.ModuleKey, "module returned an empty status response.")
                : new ModuleRegistrationRemoteStatus(parsed.Exists, parsed.RegisteredAt, parsed.HasCredentialInGracePeriod);
        }
        catch (JsonException ex)
        {
            throw new ModuleUnreachableException(module.ModuleKey, $"module returned a malformed status response: {ex.Message}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri uri, object? body, ModuleKey moduleKey, ModuleProvisioningSecret provisioningSecret,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var httpRequest = new HttpRequestMessage(method, uri);
            if (body is not null)
            {
                httpRequest.Content = JsonContent.Create(body, options: JsonOptions);
            }

            httpRequest.Headers.Add(ProvisioningSecretHeaderName, provisioningSecret.Value);
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ModuleUnreachableException(moduleKey, ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ModuleUnreachableException(moduleKey, $"module answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return response;
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

    private sealed record StatusWireResponse(bool Exists, DateTimeOffset? RegisteredAt, bool HasCredentialInGracePeriod);

    /// <summary>Byte-identical property names to
    /// <c>Ago.Calendar.Api.ChatModule.ModuleRegistrationEndpoints.TenantErasureResponse</c> under
    /// default <see cref="JsonSerializerDefaults.Web"/> options - no shared assembly between the two
    /// repositories (`adr/0093`), so this is duplicated on purpose, the identical shape every other
    /// wire DTO in this class already takes.</summary>
    private sealed record TenantErasureWireResponse(bool TenantExisted, bool Confirmed);
}
