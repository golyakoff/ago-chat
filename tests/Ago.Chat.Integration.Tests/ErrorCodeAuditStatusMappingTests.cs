using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-98`: this item's own real, one-line-per-code audit of every error code any handler in
/// `ago-chat` can produce, cross-checked against <see cref="ErrorExtensions.ToProblem"/>'s own switch,
/// found 41 codes a real HTTP endpoint could reach that had never been given a line - not the six this
/// item's own "found while building `25-91`" text named, which was a floor, not a ceiling, exactly as
/// that text warned. Thirty-three of the forty-one reach a real endpoint's own `ToProblem` call and are
/// fixed here; the other eight (seven Hub-only codes and one dead factory method with no caller at
/// all) are recorded as deliberately absent in <see cref="ErrorExtensions"/>' own switch comment rather
/// than given a line, since a line for a code nothing ever constructs would be untestable and would
/// misstate what this audit actually found.
///
/// <para><b>Same level `DemoEndpointErrorStatusMappingTests`/`ErrorExtensionsRetryAfterTests` already
/// test this method at</b> - a bare <see cref="DefaultHttpContext"/>, no hosting pipeline, since the
/// thing under test is the mapping itself, not any of the business rules that produce these codes (most
/// of which already have their own Application-layer tests for the rule that returns the error - this
/// file is not re-testing "why", only "what status").</para>
/// </summary>
public sealed class ErrorCodeAuditStatusMappingTests
{
    [Fact]
    public async Task ToProblem_AcceptanceInvalid_Returns400BadRequest() =>
        await AssertMapsToAsync(AcceptanceErrors.Invalid("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_AiAddOnForbidden_Returns403Forbidden() =>
        await AssertMapsToAsync(AiAddOnErrors.Forbidden("reason"), StatusCodes.Status403Forbidden);

    [Fact]
    public async Task ToProblem_AiAddOnNotPurchased_Returns402PaymentRequired() =>
        await AssertMapsToAsync(AiAddOnErrors.NotPurchased("reason"), StatusCodes.Status402PaymentRequired);

    [Fact]
    public async Task ToProblem_AiAddOnAgreementNotPublished_Returns503ServiceUnavailable() =>
        await AssertMapsToAsync(AiAddOnErrors.AgreementNotPublished("reason"), StatusCodes.Status503ServiceUnavailable);

    [Fact]
    public async Task ToProblem_AiAddOnAgreementNotAccepted_Returns409Conflict() =>
        await AssertMapsToAsync(AiAddOnErrors.AgreementNotAccepted("reason"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_AiAddOnBasisNotDeclared_Returns409Conflict() =>
        await AssertMapsToAsync(AiAddOnErrors.BasisNotDeclared("reason"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_AiAddOnAgreementVersionStale_Returns409Conflict() =>
        await AssertMapsToAsync(AiAddOnErrors.AgreementVersionStale("reason"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_AssignmentPenaltyInvalid_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.AssignmentPenaltyInvalid("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_AttachmentConversationBudgetExceeded_Returns413PayloadTooLarge() =>
        await AssertMapsToAsync(
            ConversationErrors.AttachmentConversationBudgetExceeded(1024, 512), StatusCodes.Status413PayloadTooLarge);

    [Fact]
    public async Task ToProblem_AttachmentSiteBudgetExceeded_Returns413PayloadTooLarge() =>
        await AssertMapsToAsync(
            ConversationErrors.AttachmentSiteBudgetExceeded(1024, 512), StatusCodes.Status413PayloadTooLarge);

    [Fact]
    public async Task ToProblem_BillingInvalidSeatCount_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.BillingInvalidSeatCount("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_BillingPaymentProviderRefused_Returns402PaymentRequired() =>
        await AssertMapsToAsync(ConversationErrors.BillingPaymentProviderRefused("reason"), StatusCodes.Status402PaymentRequired);

    [Fact]
    public async Task ToProblem_BillingSeatCountUnchanged_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.BillingSeatCountUnchanged(), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_BillingSubscriptionNotActive_Returns409Conflict() =>
        await AssertMapsToAsync(ConversationErrors.BillingSubscriptionNotActive("reason"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_BillingSubscriptionNotFound_Returns404NotFound() =>
        await AssertMapsToAsync(ConversationErrors.BillingSubscriptionNotFound(Guid.NewGuid()), StatusCodes.Status404NotFound);

    [Fact]
    public async Task ToProblem_ContactVisibilityInvalidRung_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.ContactVisibilityInvalidRung("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_DocumentInvalid_Returns400BadRequest() =>
        await AssertMapsToAsync(PublishedDocumentErrors.Invalid("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_DocumentNotFound_Returns404NotFound() =>
        await AssertMapsToAsync(PublishedDocumentErrors.NotFound("some-key"), StatusCodes.Status404NotFound);

    [Fact]
    public async Task ToProblem_DocumentPublishConflict_Returns409Conflict() =>
        await AssertMapsToAsync(PublishedDocumentErrors.PublishConflict("some-key"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_MessageArchiveNotFound_Returns404NotFound() =>
        await AssertMapsToAsync(
            ConversationErrors.MessageArchiveNotFound("hot", new DateOnly(2026, 1, 1)), StatusCodes.Status404NotFound);

    [Fact]
    public async Task ToProblem_ModuleTriggerWordAlreadyRegistered_Returns409Conflict() =>
        await AssertMapsToAsync(
            ConversationErrors.ModuleTriggerWordAlreadyRegistered("book", "other-module"), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_ModuleTriggerWordReserved_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.ModuleTriggerWordReserved("help"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_ModuleTaskChannelPriorityChannelNotEligible_Returns404NotFound() =>
        await AssertMapsToAsync(ConversationErrors.ModuleTaskChannelNotEligible(Guid.NewGuid()), StatusCodes.Status404NotFound);

    [Fact]
    public async Task ToProblem_ModuleTaskChannelPriorityDuplicateEntry_Returns400BadRequest() =>
        await AssertMapsToAsync(
            ConversationErrors.ModuleTaskChannelPriorityDuplicateEntry(Guid.NewGuid()), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_ModuleTaskChannelPriorityNoActiveTask_Returns409Conflict() =>
        await AssertMapsToAsync(ConversationErrors.ModuleTaskChannelPriorityNoActiveTask(), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_OfflineAutoReplyInvalid_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.OfflineAutoReplyInvalid("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_OperatorSeatLimitReached_Returns402PaymentRequired() =>
        await AssertMapsToAsync(ConversationErrors.OperatorSeatLimitReached(5), StatusCodes.Status402PaymentRequired);

    [Fact]
    public async Task ToProblem_VisitorNotRestricted_Returns409Conflict() =>
        await AssertMapsToAsync(ConversationErrors.VisitorNotRestricted(Guid.NewGuid()), StatusCodes.Status409Conflict);

    [Fact]
    public async Task ToProblem_WidgetConfigInvalidAutoOpenDelay_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.WidgetConfigInvalidAutoOpenDelay("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_WidgetConfigInvalidAutoOpenGreetingText_Returns400BadRequest() =>
        await AssertMapsToAsync(
            ConversationErrors.WidgetConfigInvalidAutoOpenGreetingText("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_WidgetConfigInvalidLocale_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.WidgetConfigInvalidLocale("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_WidgetConfigInvalidNoticeText_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.WidgetConfigInvalidNoticeText("reason"), StatusCodes.Status400BadRequest);

    [Fact]
    public async Task ToProblem_WidgetConfigInvalidNoticeUrl_Returns400BadRequest() =>
        await AssertMapsToAsync(ConversationErrors.WidgetConfigInvalidNoticeUrl("reason"), StatusCodes.Status400BadRequest);

    private static async Task AssertMapsToAsync(Error error, int expectedStatusCode)
    {
        // `DemoEndpointErrorStatusMappingTests`' own precedent for this exact minimal set -
        // `Result.ExecuteAsync` (`ProblemHttpResult` included) resolves services off
        // `HttpContext.RequestServices` to serialize the response, which `DefaultHttpContext` leaves
        // null by default.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        var result = error.ToProblem(httpContext);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);

        // `type`/`title` are API contract (api-design.md) - proves the code itself still rides along
        // unchanged, not only the status.
        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        Assert.Contains($"\"{error.Code}\"", body, StringComparison.Ordinal);
    }
}
