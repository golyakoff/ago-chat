using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.MintVisitorChannelLinkCode;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Telegram;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-148`: the full Telegram identity-continuity chain against real Postgres, composed from real
/// production types throughout - `MintVisitorChannelLinkCodeHandler` (the exact handler
/// <c>AuthEndpoints</c> calls), the real <see cref="TelegramInboundMessageParser"/> (not a hand-simulated
/// bare code), and <see cref="ReceiveChannelMessageHandler"/>'s own confirmation branch - the identical
/// "wire the real handlers together against real Postgres, fake only the queueing"
/// <see cref="SynchronousMessagePipeline"/> technique <see cref="ReceiveChannelMessageDrainedByTheRealPipelineTests"/>
/// and <see cref="VkWebhookEndpointsTests"/> already establish.
///
/// <para><b>What this proves, and what it honestly does not.</b> This is the maximum this item's own
/// suite can prove without a real Telegram bot token and a real deployed webhook to tap a real deep link
/// against - the identical honesty this codebase already states for VK/MAX ("no token was available while
/// this item was built", `VkDtos.cs`'s own remarks). What it does prove, against a real database: a code
/// minted by the exact handler the visitor-session handshake calls, run through the exact parsing
/// Telegram's own `/start` deep link produces, reaches the exact confirmation branch `/linkidentity`'s own
/// hand-typed flow already uses - one mechanism, never a second, parallel one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TelegramLinkContinuityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The item's own headline proof: a visitor already mid-widget-conversation has a Telegram link code
    /// minted for their session; Telegram's own `/start &lt;code&gt;` wire form links the *same* visitor's
    /// identity, not a new one - the identical guarantee this codebase already gives a visitor who types
    /// the bare code by hand, reached without ever typing anything.
    /// </summary>
    [Fact]
    public async Task OpeningTheMintedLink_AndSendingTelegramsOwnStartMessage_LinksTheSameVisitor()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        // The visitor session mint that already exists before any Telegram link is ever opened - a real
        // Visitor and a real, already-open widget conversation, exactly the scenario AuthEndpoints' own
        // ChannelLinks field is minted for.
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var mintDb = fixture.CreateDbContext();
        var mintHandler = new MintVisitorChannelLinkCodeHandler(
            new VisitorRepository(mintDb), new PendingChannelLinkRequestRepository(mintDb), new PendingChannelLinkCodeGenerator(),
            new PendingChannelLinkRequestOptions(), new UuidV7Generator(), new SystemClock());
        var minted = await mintHandler.HandleAsync(
            new MintVisitorChannelLinkCode(siteId, visitorId, ChannelKind.Telegram), CancellationToken.None);
        Assert.NotNull(minted);

        // Telegram's own wire form for opening `t.me/<bot>?start=<code>` - parsed by the real parser, not
        // hand-simulated, so a regression in the prefix-stripping logic itself would fail this test too.
        var update = new TelegramUpdate(
            UpdateId: 1,
            Message: new TelegramMessage(
                MessageId: 55, From: new TelegramUser(Id: 987654), Chat: new TelegramChat(Id: 987654),
                Text: $"/start {minted.Code}"));
        var parsed = TelegramInboundMessageParser.TryParse(update);
        Assert.NotNull(parsed);

        await using var db2 = fixture.CreateDbContext();
        var identities = new ChannelIdentityRepository(db2);
        var visitors = new VisitorRepository(db2);
        var conversations = new ConversationRepository(db2);
        var clock = new SystemClock();
        var idGenerator = new UuidV7Generator();
        var emojiPairs = new VisitorEmojiPairGenerator();
        var pipeline = new SynchronousMessagePipeline(fixture.DataSource);

        var receiveHandler = new ReceiveChannelMessageHandler(
            identities,
            visitors,
            new PendingChannelLinkRequestRepository(db2),
            new StartConversationHandler(
                visitors, conversations, new VisitorRestrictionRepository(fixture.DataSource),
                new GetSiteConfigByIdHandler(new SiteRepository(db2), new NoOpCache()),
                new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), clock, idGenerator, emojiPairs),
            new SendVisitorMessageHandler(
                conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline),
            new AlwaysEntitledBillingOptionEntitlementProvider(),
            new AlwaysEntitledModuleQuantityGrantStore(),
            clock,
            idGenerator,
            emojiPairs);

        var result = await receiveHandler.HandleAsync(
            new ReceiveChannelMessage(
                siteId, ChannelKind.Telegram, new ExternalChannelAddress(parsed.ChatId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId), parsed.Text!),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : "");
        Assert.Equal(visitorId, result.Value.VisitorId);
        Assert.False(result.Value.VisitorWasNew);

        await using var verifyDb = fixture.CreateDbContext();
        var identity = await new ChannelIdentityRepository(verifyDb).FindAsync(
            siteId, ChannelKind.Telegram, new ExternalChannelAddress(parsed.ChatId.ToString()), CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal(visitorId, identity!.VisitorId);

        var pending = await new PendingChannelLinkRequestRepository(verifyDb).FindLiveAsync(
            siteId, ChannelKind.Telegram, SHA256Hash(minted.Code), Now, CancellationToken.None);
        // Already consumed - FindLiveAsync must not find it as still live any more.
        Assert.Null(pending);
    }

    /// <summary>The item's own named failure-mode requirement: an expired minted code fails exactly the
    /// way `/linkidentity`'s own expired-code path already does - falling through to an ordinary new
    /// visitor, never a second, distinguishable failure.</summary>
    [Fact]
    public async Task OpeningTheMintedLink_AfterTheCodeExpires_FallsThroughToTheOrdinaryNewVisitorPath()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Minted already expired - a fixed clock in the past relative to "now" below, the same shape
        // ReceiveChannelMessageHandlerTests' own expired-code test uses, adapted to real Postgres (which
        // has no settable clock of its own, so the request is minted with a validity window that has
        // already elapsed by the time it is looked up).
        var options = new PendingChannelLinkRequestOptions { ValidFor = TimeSpan.FromMilliseconds(1) };
        await using var mintDb = fixture.CreateDbContext();
        var mintHandler = new MintVisitorChannelLinkCodeHandler(
            new VisitorRepository(mintDb), new PendingChannelLinkRequestRepository(mintDb), new PendingChannelLinkCodeGenerator(),
            options, new UuidV7Generator(), new SystemClock());
        var minted = await mintHandler.HandleAsync(
            new MintVisitorChannelLinkCode(siteId, visitorId, ChannelKind.Telegram), CancellationToken.None);
        Assert.NotNull(minted);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var update = new TelegramUpdate(
            UpdateId: 1,
            Message: new TelegramMessage(
                MessageId: 55, From: new TelegramUser(Id: 987655), Chat: new TelegramChat(Id: 987655),
                Text: $"/start {minted.Code}"));
        var parsed = TelegramInboundMessageParser.TryParse(update);
        Assert.NotNull(parsed);

        await using var db2 = fixture.CreateDbContext();
        var identities = new ChannelIdentityRepository(db2);
        var visitors = new VisitorRepository(db2);
        var conversations = new ConversationRepository(db2);
        var clock = new SystemClock();
        var idGenerator = new UuidV7Generator();
        var emojiPairs = new VisitorEmojiPairGenerator();
        var pipeline = new SynchronousMessagePipeline(fixture.DataSource);

        var receiveHandler = new ReceiveChannelMessageHandler(
            identities,
            visitors,
            new PendingChannelLinkRequestRepository(db2),
            new StartConversationHandler(
                visitors, conversations, new VisitorRestrictionRepository(fixture.DataSource),
                new GetSiteConfigByIdHandler(new SiteRepository(db2), new NoOpCache()),
                new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), clock, idGenerator, emojiPairs),
            new SendVisitorMessageHandler(
                conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline),
            new AlwaysEntitledBillingOptionEntitlementProvider(),
            new AlwaysEntitledModuleQuantityGrantStore(),
            clock,
            idGenerator,
            emojiPairs);

        var result = await receiveHandler.HandleAsync(
            new ReceiveChannelMessage(
                siteId, ChannelKind.Telegram, new ExternalChannelAddress(parsed.ChatId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId), parsed.Text!),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : "");
        Assert.True(result.Value.VisitorWasNew);
        Assert.NotEqual(visitorId, result.Value.VisitorId);
    }

    private static byte[] SHA256Hash(string value) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
}
