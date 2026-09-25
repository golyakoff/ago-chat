using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.RecordChannelVisitorContact;

/// <summary>
/// `25-151`: the end-to-end proof that a contact shared on a text channel stops being thrown away -
/// this handler's own class remarks describe why it duplicates
/// <c>ReceiveChannelMessageHandler</c>'s identity-resolution branch rather than sharing it, and why it
/// composes <c>RecordVisitorContactDetailHandler.HandleAsVisitorAsync</c> instead of writing a
/// <see cref="VisitorContactDetail"/> directly. What each group below defends:
/// <list type="bullet">
/// <item><b>Recording</b> - a verified contact becomes a <see cref="VisitorContactDetailKind.Phone"/>
/// row (and a <see cref="VisitorContactDetailKind.Name"/> one, if given) for the resolved visitor.</item>
/// <item><b>Resolution</b> - a brand-new address mints a visitor exactly the way an ordinary channel
/// message would; an existing one resolves to the same visitor and conversation.</item>
/// <item><b>Consent</b> - `24-05`'s gate refuses the write cleanly (no exception, no visitor-facing
/// error) on a site that requires it and has none on file - the one thing this item's own backlog is
/// explicit it does not have to fix, only not break on.</item>
/// </list>
/// </summary>
public class RecordChannelVisitorContactHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContactHandler Handler,
        FakeChannelIdentityRepository Identities,
        FakeVisitorRepository Visitors,
        FakeConversationRepository Conversations,
        FakeVisitorContactDetailRepository ContactDetails,
        FakeAcceptanceRepository Acceptances,
        FakeClock Clock);

    private static Fixture CreateFixture(bool requireContactConsent = false)
    {
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var emojiPairs = new FakeVisitorEmojiPairGenerator();

        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        if (requireContactConsent)
        {
            site.UpdateWidgetConfig(
                new WidgetConfig(site.WidgetConfig.PrimaryColorHex, site.WidgetConfig.Position, requireContactConsent: true), Now);
        }

        sites.Seed(site);

        var acceptances = new FakeAcceptanceRepository();
        var contactDetails = new FakeVisitorContactDetailRepository();
        var outbox = new FakeOutboxWriter();
        var permissions = new FakePermissionChecker();

        var startConversation = new StartConversationHandler(
            visitors, conversations, new FakeVisitorRestrictionRepository(),
            new GetSiteConfigByIdHandler(sites, new FakeCache()),
            new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), clock, idGenerator, emojiPairs);

        var recordContactDetail = new RecordVisitorContactDetailHandler(
            conversations, contactDetails, sites, acceptances, permissions, new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), idGenerator, clock);

        var handler = new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContactHandler(
            identities, visitors, startConversation, recordContactDetail, clock, idGenerator, emojiPairs,
            NullLogger<Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContactHandler>.Instance);

        return new Fixture(handler, identities, visitors, conversations, contactDetails, acceptances, clock);
    }

    // -----------------------------------------------------------------------------------------
    // Recording
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AVerifiedContact_RecordsAPhoneDetailForTheResolvedVisitor()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", "Ada Lovelace"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var phone = Assert.Single(fixture.ContactDetails.All, d => d.Kind == VisitorContactDetailKind.Phone);
        Assert.Equal("+1 555 0100", phone.Value);
        Assert.Equal(VisitorContactDetailSource.Visitor, phone.Source);
        Assert.Null(phone.RecordedByOperatorId);
        Assert.Equal(result.Value.VisitorId, phone.VisitorId);
    }

    [Fact]
    public async Task AVerifiedContactWithAName_AlsoRecordsANameDetail()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", "Ada Lovelace"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.NameRecorded);
        var name = Assert.Single(fixture.ContactDetails.All, d => d.Kind == VisitorContactDetailKind.Name);
        Assert.Equal("Ada Lovelace", name.Value);
    }

    [Fact]
    public async Task AVerifiedContactWithNoName_RecordsOnlyThePhone()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Max, new ExternalChannelAddress("max-chat-1"), "+1 555 0100", Name: null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NameRecorded);
        Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorContactDetailKind.Phone, fixture.ContactDetails.All.Single().Kind);
    }

    // -----------------------------------------------------------------------------------------
    // Resolution - the identical "brand-new address -> mint a visitor, existing -> resolve" shape
    // ReceiveChannelMessageHandler already establishes for an ordinary message.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AContactFromABrandNewAddress_MintsAVisitorAndAConversation()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var identity = Assert.Single(fixture.Identities.All);
        Assert.Equal(result.Value.VisitorId, identity.VisitorId);
        var conversation = await fixture.Conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(conversation);
        Assert.Equal(result.Value.VisitorId, conversation!.VisitorId);
    }

    [Fact]
    public async Task AContactFromAnAddressWithAnExistingIdentity_ResolvesToTheSameVisitorAndConversation()
    {
        var fixture = CreateFixture();
        var address = new ExternalChannelAddress("tg-user-1");

        var first = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, address, "+1 555 0100", null),
            CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, address, "+1 555 0100", null),
            CancellationToken.None);

        Assert.Equal(first.Value.VisitorId, second.Value.VisitorId);
        Assert.Equal(first.Value.ConversationId, second.Value.ConversationId);
        Assert.Single(fixture.Identities.All);
    }

    // -----------------------------------------------------------------------------------------
    // Consent - `24-05`'s gate. This item's own out-of-scope: refused cleanly, not fixed.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task OnASiteRequiringConsentWithNoneOnFile_TheWriteIsRefusedCleanly_NoCrashNoRecord()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", "Ada Lovelace"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
        // The identity/visitor/conversation resolution itself is unaffected by the consent gate - only
        // the contact-detail write is refused, the same "the gate attaches to this write, never to the
        // conversation" split RecordVisitorContactDetailHandler's own remarks describe.
        Assert.Single(fixture.Identities.All);
    }

    [Fact]
    public async Task OnASiteRequiringConsentWithAnAcceptanceOnFile_TheWriteSucceeds()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var first = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", null),
            CancellationToken.None);
        Assert.True(first.IsFailure);

        var visitorId = fixture.Identities.All.Single().VisitorId;
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), visitorId, contactKey, "v1", Now),
            CancellationToken.None);

        var second = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContact(
                SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), "+1 555 0100", null),
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Single(fixture.ContactDetails.All);
    }
}
