using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`: the one class in this codebase that speaks ЮKassa's own Payments API HTTP shape - the same
/// "thin, no retry/timeout/circuit-breaker of its own" discipline <c>TelegramApiClient</c>/`MaxApiClient`
/// already establish for this codebase's other outbound third-party clients (that is `ChatModule`'s job
/// when it builds this class's <see cref="HttpClient"/>, including the Basic-auth header - see
/// `ChatModule`'s own remarks). Implements <see cref="IYooKassaPaymentsClient"/>, the provider-neutral
/// Application port; nothing above this project may reference this class or any type in this file
/// directly.
///
/// <para><b>The terminal/transient split, made concrete for ЮKassa.</b> A client-shaped refusal
/// (400/401/403/404 - a malformed request, bad credentials, a forbidden operation, an unknown resource)
/// returns a value; everything else (429, 5xx, a network fault) throws - the identical reasoned default
/// <c>TelegramApiClient</c>'s own remarks state, applied here to a second provider. <b>Not confirmed
/// against a real ЮKassa response</b> - see <c>YooKassaDtos</c>'s own remarks on why, and this item's own
/// report for what a real credential would need to verify.</para>
/// </summary>
public sealed class YooKassaPaymentsApiClient(HttpClient httpClient) : IYooKassaPaymentsClient
{
    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(new YooKassaCreatePaymentRequest(
                Amount: new YooKassaAmount(request.AmountRub.ToString("F2", CultureInfo.InvariantCulture), "RUB"),
                Capture: true,
                Confirmation: new YooKassaConfirmationRequest("redirect", request.ReturnUrl),
                SavePaymentMethod: true,
                Description: request.Description)),
        };
        httpRequest.Headers.Add("Idempotence-Key", request.IdempotenceKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<YooKassaPaymentResponse>(cancellationToken);
            if (body?.Confirmation?.ConfirmationUrl is not { Length: > 0 } confirmationUrl)
            {
                // ЮKassa answered 2xx but did not include the one field this call exists to get - not
                // a documented refusal shape, so this is treated as a transient/unexpected condition
                // rather than a silent null propagating into CreateCheckoutSessionHandler.
                throw new HttpRequestException(
                    $"ЮKassa payment creation returned {(int)response.StatusCode} with no confirmation_url.");
            }

            return new CreatePaymentResult.Success(body.Id, confirmationUrl);
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var error = await response.Content.ReadFromJsonAsync<YooKassaErrorResponse>(cancellationToken);
            return new CreatePaymentResult.Refused(
                $"ЮKassa refused the payment ({(int)response.StatusCode}): {error?.Description ?? error?.Code ?? "no reason given"}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"ЮKassa API returned {(int)response.StatusCode} for POST payments: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>`13-03`: the charge-on-file half - same endpoint, same terminal/transient split as
    /// <see cref="CreatePaymentAsync"/>, no `confirmation` object in the request and no
    /// `confirmation_url` expected back in the response (there is no browser to redirect).</summary>
    public async Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
        ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(new YooKassaChargeStoredPaymentMethodRequest(
                Amount: new YooKassaAmount(request.AmountRub.ToString("F2", CultureInfo.InvariantCulture), "RUB"),
                Capture: true,
                PaymentMethodId: request.PaymentMethodId,
                Description: request.Description)),
        };
        httpRequest.Headers.Add("Idempotence-Key", request.IdempotenceKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<YooKassaPaymentResponse>(cancellationToken);
            if (body?.Id is not { Length: > 0 } paymentId)
            {
                throw new HttpRequestException(
                    $"ЮKassa charge-on-file returned {(int)response.StatusCode} with no payment id.");
            }

            return new ChargeStoredPaymentMethodResult.Success(paymentId);
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var error = await response.Content.ReadFromJsonAsync<YooKassaErrorResponse>(cancellationToken);
            return new ChargeStoredPaymentMethodResult.Refused(
                $"ЮKassa refused the charge ({(int)response.StatusCode}): {error?.Description ?? error?.Code ?? "no reason given"}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"ЮKassa API returned {(int)response.StatusCode} for POST payments (charge on file): {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
