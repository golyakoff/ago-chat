using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.InitiatePhoneVerification;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.InitiatePhoneVerification;

public class InitiatePhoneVerificationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Phone = "+7 999 123-45-67";

    private sealed record Fixture(
        InitiatePhoneVerificationHandler Handler,
        FakePendingPhoneVerificationRepository PendingVerifications,
        FakeOutboxWriter Outbox,
        ConversationId ConversationId);

    private static Fixture CreateFixture(Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var pendingVerifications = new FakePendingPhoneVerificationRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new InitiatePhoneVerificationHandler(
            conversations, pendingVerifications, new FakePendingChannelLinkCodeGenerator("482913"),
            rateLimiter ?? new FakeRateLimiter(), outbox, new PhoneVerificationOptions(),
            new PhoneVerificationRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, pendingVerifications, outbox, conversation.Id);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_ValidRequest_SavesAPendingVerification()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.PendingVerifications.All);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal("+79991234567", saved.Phone);
        Assert.Equal(result.Value.PendingPhoneVerificationId, saved.Id.Value);
        Assert.Equal(result.Value.ExpiresAt, saved.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_ValidRequest_EnqueuesOneOutboxEventCarryingThePlaintextCode()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, Phone), CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(PhoneVerificationDeliveryRequested), envelope.Type);
        Assert.Contains("482913", envelope.Payload);
        Assert.Contains(result.Value.PendingPhoneVerificationId.ToString(), envelope.Payload);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_InvalidPhoneNumber_ReturnsInvalidNumber_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, "not-a-phone"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.InvalidNumber", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingVerifications.All);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_VisitorNotAParticipant_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, someoneElse, Phone), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingVerifications.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(new ConversationId(Guid.NewGuid()), VisitorId, Phone), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>The Done-when's own bar: a send past the phone-bucket limit never reaches the point
    /// where it would be billed - the sender is never invoked because no outbox event is ever written for
    /// `PhoneVerificationDeliveryConsumer` to act on, and no pending verification is persisted either.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_PhoneBucketExhausted_ReturnsRateLimited_AndNeverWritesAnOutboxEventOrPendingVerification()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("phone-verification:phone:", TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, Phone), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingVerifications.All);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_VisitorBucketExhausted_ReturnsRateLimited_AndNeverWritesAnOutboxEventOrPendingVerification()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("phone-verification:visitor:", TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, Phone), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingVerifications.All);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_SiteBucketExhausted_ReturnsRateLimited_AndNeverWritesAnOutboxEventOrPendingVerification()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("phone-verification:site:", TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, Phone), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingVerifications.All);
        Assert.Empty(fixture.Outbox.Enqueued);
    }
}
