using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `24-05`'s own central claim, proven rather than asserted: **the gate attaches to handing over a
/// contact detail, never to the conversation**. One site, one visitor, one conversation, with
/// <see cref="WidgetConfig.RequireContactConsent"/> turned on and no acceptance recorded at all - the
/// worst case for a visitor who refuses. This test proves both halves in one place: the contact-detail
/// write is refused, and an ordinary message on the *same* conversation still succeeds, unaffected -
/// the exact claim the backlog item's own Done-when names as "the half that would quietly become a
/// gate on the product".
///
/// <para>Structurally, this is not a coincidence <see cref="SendVisitorMessageHandler"/> happens to
/// pass: that handler's own constructor takes no <see cref="Application.Abstractions.ISiteRepository"/>
/// and no <see cref="Application.Abstractions.IAcceptanceRepository"/> at all, so there is no field
/// this gate could even read from that code path - `TenantScopeExemptions`'s own entry for
/// <c>SendVisitorMessageHandler.HandleAsync</c> already says as much for a different reason
/// (authorization, not consent), and this test is the same fact's other half, proven at runtime.</para>
/// </summary>
public class ConsentGateDoesNotBlockConversationTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARefusingVisitor_CanStillSendAMessage_WhileTheirContactDetailWriteIsRefused()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), Now);
        sites.Seed(site);

        var contactDetails = new FakeVisitorContactDetailRepository();
        var acceptances = new FakeAcceptanceRepository();
        var contactHandler = new RecordVisitorContactDetailHandler(
            conversations, contactDetails, sites, acceptances, new FakePermissionChecker(), new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        var pipeline = new FakeMessagePipeline();
        var messageHandler = new SendVisitorMessageHandler(
            conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);

        // The refusal this item's own crux is about: no consent recorded anywhere for this visitor.
        var contactResult = await contactHandler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(conversation.Id, VisitorId, "Phone", "+1 555 0177"), CancellationToken.None);

        Assert.True(contactResult.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", contactResult.Error!.Value.Code);
        Assert.Empty(contactDetails.All);

        // The claim: refusing costs them the contact-detail write, and nothing else. The conversation
        // itself - opening it, reading the auto-reply, typing "do you have this in blue?" - stays
        // reachable exactly as if RequireContactConsent had never been turned on.
        var messageResult = await messageHandler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "do you have this in blue?"), CancellationToken.None);

        Assert.True(messageResult.IsSuccess);
        Assert.Single(pipeline.Enqueued);
    }
}
