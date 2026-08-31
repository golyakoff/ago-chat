using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ConfirmPhoneVerification;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ConfirmPhoneVerification;

public class ConfirmPhoneVerificationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Code = "482913";
    private const string CanonicalPhone = "+79991234567";

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private sealed record Fixture(
        ConfirmPhoneVerificationHandler Handler,
        FakePendingPhoneVerificationRepository PendingVerifications,
        FakeChannelIdentityRepository ChannelIdentities,
        ConversationId ConversationId,
        PendingPhoneVerificationId PendingPhoneVerificationId);

    private static async Task<Fixture> CreateFixtureAsync(
        int maxAttempts = 5, TimeSpan? validFor = null, VisitorId? verificationVisitorId = null,
        SiteId? verificationSiteId = null, ChannelIdentity? existingIdentity = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var verification = PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(Guid.NewGuid()), verificationSiteId ?? SiteId, verificationVisitorId ?? VisitorId,
            new PhoneNumber(CanonicalPhone), Code, Hash(Code), PhoneVerificationDeliveryMethod.Sms, Now,
            validFor ?? TimeSpan.FromMinutes(10), maxAttempts);
        var pendingVerifications = new FakePendingPhoneVerificationRepository();
        pendingVerifications.Seed(verification);

        var channelIdentities = new FakeChannelIdentityRepository();
        if (existingIdentity is not null)
        {
            await channelIdentities.SaveAsync(existingIdentity, CancellationToken.None);
        }

        var handler = new ConfirmPhoneVerificationHandler(
            conversations, pendingVerifications, channelIdentities, new FakeIdGenerator(), new FakeClock(Now.AddMinutes(1)));

        return new Fixture(handler, pendingVerifications, channelIdentities, conversation.Id, verification.Id);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_CorrectCode_ReturnsSuccess_AndLinksARealChannelIdentity()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WasNewlyLinked);
        var identity = Assert.Single(fixture.ChannelIdentities.All);
        Assert.Equal(result.Value.ChannelIdentityId, identity.Id.Value);
        Assert.Equal(SiteId, identity.SiteId);
        Assert.Equal(ChannelKind.Sms, identity.Kind);
        Assert.Equal(CanonicalPhone, identity.Address.Value);
        Assert.Equal(VisitorId, identity.VisitorId);
        Assert.True(identity.Active);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_CorrectCode_MarksThePendingVerificationConsumed()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        var saved = fixture.PendingVerifications.All.Single();
        Assert.NotNull(saved.ConsumedAt);
    }

    /// <summary>Done-when: "a second verification of the same number reuses the existing identity rather
    /// than creating a duplicate" - proven here by seeding an already-linked, active identity for the same
    /// (site, kind, address, visitor) before confirming a second, independent pending verification for the
    /// identical number.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_PhoneAlreadyVerifiedForTheSameVisitor_ReusesTheExistingIdentity_NoDuplicateRow()
    {
        var existingIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms, new ExternalChannelAddress(CanonicalPhone),
            VisitorId, Now.AddDays(-1));
        var fixture = await CreateFixtureAsync(existingIdentity: existingIdentity);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.WasNewlyLinked);
        Assert.Equal(existingIdentity.Id.Value, result.Value.ChannelIdentityId);
        var onlyIdentity = Assert.Single(fixture.ChannelIdentities.All);
        Assert.Equal(existingIdentity.Id, onlyIdentity.Id);
        Assert.Equal(Now.AddMinutes(1), onlyIdentity.LastSeenAt);
    }

    /// <summary>`adr/0079` decision 3, applied to this item's own confirmation path - refused, not
    /// merged, and no second active row is created for the same address.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_PhoneAlreadyVerifiedForADifferentVisitor_ReturnsAlreadyLinked_AndDoesNotMutateTheExistingIdentity()
    {
        var otherVisitorId = new VisitorId(Guid.NewGuid());
        var existingIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms, new ExternalChannelAddress(CanonicalPhone),
            otherVisitorId, Now.AddDays(-1));
        var fixture = await CreateFixtureAsync(existingIdentity: existingIdentity);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.AlreadyLinkedToAnotherVisitor", result.Error!.Value.Code);
        var onlyIdentity = Assert.Single(fixture.ChannelIdentities.All);
        Assert.Equal(otherVisitorId, onlyIdentity.VisitorId);
        Assert.Equal(Now.AddDays(-1), onlyIdentity.LastSeenAt);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WrongCode_ReturnsWrongCode_AndDoesNotLinkAnIdentity()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, "000000"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.WrongCode", result.Error!.Value.Code);
        Assert.Empty(fixture.ChannelIdentities.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WrongCode_PersistsTheIncrementedAttemptCount()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, "000000"),
            CancellationToken.None);

        var saved = fixture.PendingVerifications.All.Single();
        Assert.Equal(1, saved.AttemptCount);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_ExpiredCode_ReturnsExpired()
    {
        var fixture = await CreateFixtureAsync(validFor: TimeSpan.FromSeconds(1));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.Expired", result.Error!.Value.Code);
        Assert.Empty(fixture.ChannelIdentities.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_TooManyWrongAttempts_ReturnsLockedOut_AndTheCorrectCodeNoLongerWorks()
    {
        var fixture = await CreateFixtureAsync(maxAttempts: 1);

        var wrongAttempt = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, "000000"),
            CancellationToken.None);
        Assert.Equal("PhoneVerification.LockedOut", wrongAttempt.Error!.Value.Code);

        var correctAttempt = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(correctAttempt.IsFailure);
        Assert.Equal("PhoneVerification.LockedOut", correctAttempt.Error!.Value.Code);
        Assert.Empty(fixture.ChannelIdentities.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_VisitorNotAParticipant_ReturnsForbidden()
    {
        var fixture = await CreateFixtureAsync();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, someoneElse, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnknownPendingVerification_ReturnsPhoneVerificationNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(
                fixture.ConversationId, VisitorId, new PendingPhoneVerificationId(Guid.NewGuid()), Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Cross-visitor isolation: a real pending verification id that belongs to a *different*
    /// visitor than the one making this call must read exactly like no such row - the same info-hiding
    /// shape this codebase's every other cross-tenant guard already uses, applied here across visitors
    /// rather than sites (`ConfirmPhoneVerificationHandler`'s own remarks on why this check exists at
    /// all, unlike `14-12`'s own confirmation branch).</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_PendingVerificationBelongsToADifferentVisitor_ReturnsPhoneVerificationNotFound()
    {
        var otherVisitorId = new VisitorId(Guid.NewGuid());
        var fixture = await CreateFixtureAsync(verificationVisitorId: otherVisitorId);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_PendingVerificationBelongsToADifferentSite_ReturnsPhoneVerificationNotFound()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = await CreateFixtureAsync(verificationSiteId: otherSiteId, verificationVisitorId: VisitorId);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(fixture.ConversationId, VisitorId, fixture.PendingPhoneVerificationId, Code),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PhoneVerification.NotFound", result.Error!.Value.Code);
    }
}
