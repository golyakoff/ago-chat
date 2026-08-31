using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.Domain.Tests;

public class PendingPhoneVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private const string Code = "482913";
    private const int DefaultMaxAttempts = 5;

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static PendingPhoneVerification Request(
        TimeSpan? validFor = null, int maxAttempts = DefaultMaxAttempts, string code = Code) =>
        PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(Guid.NewGuid()), SiteId, VisitorId, new PhoneNumber("+79991234567"),
            code, Hash(code), PhoneVerificationDeliveryMethod.Sms, Now, validFor ?? TimeSpan.FromMinutes(10),
            maxAttempts);

    [Fact]
    public void Request_StartsUnconsumed()
    {
        var verification = Request();

        Assert.Null(verification.ConsumedAt);
    }

    [Fact]
    public void Request_SetsExpiresAtToNowPlusValidFor()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        Assert.Equal(Now + TimeSpan.FromMinutes(10), verification.ExpiresAt);
    }

    [Fact]
    public void Request_StartsWithZeroAttempts()
    {
        var verification = Request();

        Assert.Equal(0, verification.AttemptCount);
    }

    [Fact]
    public void Request_StoresCanonicalPhoneValue()
    {
        var verification = PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(Guid.NewGuid()), SiteId, VisitorId,
            new PhoneNumber("+7 (999) 123-45-67"), Code, Hash(Code), PhoneVerificationDeliveryMethod.Sms, Now,
            TimeSpan.FromMinutes(10), DefaultMaxAttempts);

        Assert.Equal("+79991234567", verification.Phone);
    }

    [Fact]
    public void Request_RaisesPhoneVerificationCodeIssuedWithThePlaintextCode()
    {
        var verification = Request(code: Code);

        var raised = Assert.Single(verification.DomainEvents.OfType<PhoneVerificationCodeIssued>());
        Assert.Equal(Code, raised.Code);
        Assert.Equal(verification.Id, raised.PendingPhoneVerificationId);
        Assert.Equal(SiteId, raised.SiteId);
        Assert.Equal("+79991234567", raised.Phone);
        Assert.Equal(PhoneVerificationDeliveryMethod.Sms, raised.DeliveryMethod);
        Assert.Equal(Now, raised.OccurredAt);
    }

    [Fact]
    public void ClearDomainEvents_RemovesRaisedEvents()
    {
        var verification = Request();

        verification.ClearDomainEvents();

        Assert.Empty(verification.DomainEvents);
    }

    [Fact]
    public void IsLive_BeforeExpiryAndUnconsumed_IsTrue()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        Assert.True(verification.IsLive(Now.AddMinutes(1)));
    }

    /// <summary>`>=`, not `>` - the same boundary contract `PendingChannelLinkRequest.IsLive`/
    /// `OperatorInvite.IsExpired` already use.</summary>
    [Fact]
    public void IsLive_AtExactlyExpiresAt_IsFalse()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        Assert.False(verification.IsLive(verification.ExpiresAt));
    }

    [Fact]
    public void IsLive_AfterConfirm_IsFalse()
    {
        var verification = Request(TimeSpan.FromMinutes(10));
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        Assert.False(verification.IsLive(Now.AddMinutes(2)));
    }

    [Fact]
    public void IsLive_WhenLockedOut_IsFalse()
    {
        var verification = Request(maxAttempts: 1);
        verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));

        Assert.False(verification.IsLive(Now.AddSeconds(2)));
    }

    [Fact]
    public void AttemptConfirm_WithCorrectCode_ReturnsConfirmed()
    {
        var verification = Request(code: Code);

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.Confirmed, outcome);
    }

    [Fact]
    public void AttemptConfirm_WithCorrectCode_SetsConsumedAt()
    {
        var verification = Request(code: Code);
        var confirmedAt = Now.AddMinutes(1);

        verification.AttemptConfirm(Hash(Code), confirmedAt);

        Assert.Equal(confirmedAt, verification.ConsumedAt);
    }

    [Fact]
    public void AttemptConfirm_WithWrongCode_ReturnsWrongCode()
    {
        var verification = Request(code: Code);

        var outcome = verification.AttemptConfirm(Hash("000000"), Now.AddMinutes(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.WrongCode, outcome);
    }

    [Fact]
    public void AttemptConfirm_WithWrongCode_IncrementsAttemptCount()
    {
        var verification = Request(code: Code);

        verification.AttemptConfirm(Hash("000000"), Now.AddMinutes(1));

        Assert.Equal(1, verification.AttemptCount);
    }

    [Fact]
    public void AttemptConfirm_WhenAlreadyConsumed_ReturnsAlreadyConsumed()
    {
        var verification = Request(code: Code);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddMinutes(2));

        Assert.Equal(PhoneVerificationConfirmOutcome.AlreadyConsumed, outcome);
    }

    [Fact]
    public void AttemptConfirm_WhenExpired_ReturnsExpired()
    {
        var verification = Request(TimeSpan.FromMinutes(10), code: Code);

        var outcome = verification.AttemptConfirm(Hash(Code), verification.ExpiresAt);

        Assert.Equal(PhoneVerificationConfirmOutcome.Expired, outcome);
    }

    /// <summary>An expired row never spends an attempt on a wrong guess either - checked before the code
    /// comparison (<see cref="PendingPhoneVerification.AttemptConfirm"/>'s own remarks on check
    /// ordering).</summary>
    [Fact]
    public void AttemptConfirm_WhenExpiredWithWrongCode_DoesNotIncrementAttemptCount()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        verification.AttemptConfirm(Hash("000000"), verification.ExpiresAt);

        Assert.Equal(0, verification.AttemptCount);
    }

    [Fact]
    public void AttemptConfirm_AfterMaxWrongAttempts_ReturnsLockedOut()
    {
        var verification = Request(maxAttempts: 2);
        verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));
        verification.AttemptConfirm(Hash("111111"), Now.AddSeconds(2));

        var outcome = verification.AttemptConfirm(Hash("222222"), Now.AddSeconds(3));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }

    /// <summary>The wrong guess that pushes <see cref="PendingPhoneVerification.AttemptCount"/> to
    /// <see cref="PendingPhoneVerification.MaxAttempts"/> itself reports as <c>LockedOut</c>, not
    /// <c>WrongCode</c> - the caller learns the phone is now locked on the same call that caused it,
    /// rather than only on the next attempt.</summary>
    [Fact]
    public void AttemptConfirm_TheWrongGuessThatReachesMaxAttempts_ReturnsLockedOutNotWrongCode()
    {
        var verification = Request(maxAttempts: 1);

        var outcome = verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }

    [Fact]
    public void AttemptConfirm_EvenWithCorrectCode_WhenAlreadyLockedOut_ReturnsLockedOut()
    {
        var verification = Request(maxAttempts: 1, code: Code);
        verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddSeconds(2));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }
}
