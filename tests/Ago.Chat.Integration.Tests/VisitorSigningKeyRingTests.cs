using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-03`/`adr/0067`: the key ring's own rules, at the level they are actually decided.
///
/// <para>No fixture and no container - this is a set of keys, a clock and a `TimeSpan`. The
/// end-to-end half, where a real <c>JwtBearer</c> handler accepts or rejects a real token across a
/// rotation, is <see cref="VisitorKeyRotationTests"/>; both halves exist because either one alone
/// proves the wrong thing. A ring that returns the right list and is wired to nothing rotates
/// nothing, and an end-to-end pass cannot tell you *why* it passed.</para>
///
/// <para>The four properties that carry the item are, in order: exactly one key issues; several keys
/// validate; a retired key leaves the validation set when its drain window closes; and that window is
/// configuration rather than a constant.</para>
/// </summary>
public class VisitorSigningKeyRingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------------------
    // Exactly one key issues.

    [Fact]
    public void IssuesWithTheOneKeyThatHasNoRetiredAt()
    {
        var retired = NewKey();
        var active = NewKey();
        var ring = Ring(Now, TimeSpan.FromDays(7),
            Entry("old", retired, Now.AddHours(-1)),
            Entry("new", active));

        // Both the identity of the key and its label: a rotation is followed by reading `kid` off a
        // freshly minted token, and that only means anything if the two agree.
        Assert.Equal("new", ring.Signing.Key.KeyId);
        Assert.Equal(active, Base64Of(ring.Signing.Key));
        Assert.NotEqual(retired, Base64Of(ring.Signing.Key));
    }

    [Fact]
    public void TwoKeysWithNoRetiredAt_RefusesToStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Ring(
            Now, TimeSpan.FromDays(7), Entry("a", NewKey()), Entry("b", NewKey())));

        Assert.Contains("exactly one key with no RetiredAt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKeyRetired_RefusesToStart()
    {
        // The half-finished rotation that retires the outgoing key and forgets to add the incoming
        // one. A ring that quietly picked "the most recently retired" would keep signing with a key
        // the operator believes is out of service.
        Assert.Throws<InvalidOperationException>(() => Ring(
            Now, TimeSpan.FromDays(7),
            Entry("a", NewKey(), Now.AddHours(-2)),
            Entry("b", NewKey(), Now.AddHours(-1))));
    }

    // ------------------------------------------------------------------------------------------
    // Several keys validate, and a retired one leaves on its own.

    [Fact]
    public void ARetiredKeyStaysInTheValidationSetUntilItsDrainWindowCloses()
    {
        var retired = NewKey();
        var clock = new MutableClock(Now);
        var ring = Ring(clock, TimeSpan.FromDays(7), Entry("old", retired, Now), Entry("new", NewKey()));

        // Immediately after the rotation: both accepted. This is the property that makes rotation
        // survivable - every visitor holding a token signed an instant ago keeps working.
        Assert.Equal(2, ring.ValidationKeys().Count);
        Assert.Contains(ring.ValidationKeys(), key => Base64Of(key) == retired);

        // One second before the window closes: still both.
        clock.UtcNow = Now.AddDays(7).AddSeconds(-1);
        Assert.Equal(2, ring.ValidationKeys().Count);

        // One second after: the old key is gone, with no restart and no second deploy. That is the
        // other half of "rotatable" - a key that were accepted forever would make the rotation
        // cost-free and pointless.
        clock.UtcNow = Now.AddDays(7).AddSeconds(1);
        var remaining = Assert.Single(ring.ValidationKeys());
        Assert.Equal("new", remaining.KeyId);
    }

    [Fact]
    public void AKeyAlreadyPastItsWindowAtStartup_IsNeverInTheValidationSet()
    {
        // Not an error: configuration is edited by hand, and an entry left behind after its window
        // closed is inert rather than wrong. It must simply never be accepted.
        var ring = Ring(Now, TimeSpan.FromDays(7),
            Entry("ancient", NewKey(), Now.AddDays(-30)),
            Entry("current", NewKey()));

        var remaining = Assert.Single(ring.ValidationKeys());
        Assert.Equal("current", remaining.KeyId);
    }

    // ------------------------------------------------------------------------------------------
    // The drain window is configuration.

    [Fact]
    public void TheDrainWindowIsConfiguration_NotAConstant()
    {
        // The same keys, the same instant, two different configured windows, two different answers.
        // `17-06`/`adr/0034` set the visitor token to thirty days and `17-07`+`17-08`/`adr/0048`
        // moved it to seven; a literal in the validation path would be a number that has already
        // been wrong once and needs a release to be right again.
        var retiredKey = NewKey();
        var later = Now.AddDays(10);

        var sevenDays = Ring(later, TimeSpan.FromDays(7), Entry("old", retiredKey, Now), Entry("new", NewKey()));
        var fourteenDays = Ring(later, TimeSpan.FromDays(14), Entry("old", retiredKey, Now), Entry("new", NewKey()));

        Assert.Single(sevenDays.ValidationKeys());
        Assert.Equal(2, fourteenDays.ValidationKeys().Count);
    }

    [Fact]
    public void ADrainWindowShorterThanTheTokenLifetime_RefusesToStart()
    {
        // The setting is dangerous in exactly one direction. Too long only means a leaked old key
        // stays usable longer; too short evicts visitors holding tokens that are still legitimately
        // valid - the mass logout the whole mechanism exists to avoid, delayed by a few days and
        // therefore harder to attribute to the rotation that caused it.
        var exception = Assert.Throws<InvalidOperationException>(() => Ring(
            Now, JwtTokenService.VisitorTokenLifetime - TimeSpan.FromMinutes(1), Entry("only", NewKey())));

        Assert.Contains("shorter than the visitor token lifetime", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrainWindowEqualToTheTokenLifetime_IsAccepted()
    {
        var ring = Ring(Now, JwtTokenService.VisitorTokenLifetime, Entry("only", NewKey()));

        Assert.Single(ring.ValidationKeys());
    }

    // ------------------------------------------------------------------------------------------
    // The rest of the refusals - each one a hand-edit this configuration surface invites.

    [Fact]
    public void DuplicateIds_RefuseToStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Ring(
            Now, TimeSpan.FromDays(7), Entry("same", NewKey(), Now), Entry("same", NewKey())));

        Assert.Contains("'same'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyTooShortForHmacSha256_RefusesToStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Ring(
            Now, TimeSpan.FromDays(7),
            Entry("short", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)))));

        Assert.Contains("16 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatIsNotBase64_RefusesToStartAndTheMessageDoesNotCarryTheValue()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Ring(
            Now, TimeSpan.FromDays(7), Entry("broken", "not base64 at all")));

        Assert.Contains("'broken'", exception.Message, StringComparison.Ordinal);
        // `17-02` is the item about credentials reaching logs, and an exception message is a log
        // line. What was rejected is named by id; the value never appears.
        Assert.DoesNotContain("not base64 at all", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Reading the three configuration forms.

    [Fact]
    public void FromConfiguration_ReadsTheKeySetAndItsDrainWindow()
    {
        var active = NewKey();
        var retired = NewKey();
        var ring = VisitorSigningKeyRing.FromConfiguration(
            Configuration(new Dictionary<string, string?>
            {
                ["Auth:VisitorSigningKeys:RetirementDelay"] = "14.00:00:00",
                ["Auth:VisitorSigningKeys:Keys:0:Id"] = "2026-08",
                ["Auth:VisitorSigningKeys:Keys:0:Value"] = retired,
                ["Auth:VisitorSigningKeys:Keys:0:RetiredAt"] = "2026-08-27T12:00:00+00:00",
                ["Auth:VisitorSigningKeys:Keys:1:Id"] = "2026-09",
                ["Auth:VisitorSigningKeys:Keys:1:Value"] = active,
            }),
            new MutableClock(Now.AddDays(10)));

        Assert.Equal("2026-09", ring.Signing.Key.KeyId);
        // Ten days after the rotation with a fourteen-day window: the old key is still accepted, so
        // the configured value - not the seven-day default - is what was used.
        Assert.Equal(2, ring.ValidationKeys().Count);
    }

    [Fact]
    public void FromConfiguration_MapsTheLegacySingleKeySettingToOneActiveKey()
    {
        // The form that is deployed today. Shipping this change must rotate nothing and log nobody
        // out, which means the old setting has to keep producing exactly the key it produced before.
        var legacy = NewKey();
        var ring = VisitorSigningKeyRing.FromConfiguration(
            Configuration(new Dictionary<string, string?> { ["Auth:SigningKey"] = legacy }),
            new MutableClock(Now));

        Assert.Equal(legacy, Base64Of(ring.Signing.Key));
        Assert.Equal(VisitorSigningKeyRing.LegacyKeyId, ring.Signing.Key.KeyId);
        Assert.Single(ring.ValidationKeys());
    }

    [Fact]
    public void FromConfiguration_RefusesBothFormsAtOnce()
    {
        // The half-finished rotation edit: the key set added, the old setting left behind. Preferring
        // one silently would make that edit look applied while the host signed with the other key.
        var exception = Assert.Throws<InvalidOperationException>(() => VisitorSigningKeyRing.FromConfiguration(
            Configuration(new Dictionary<string, string?>
            {
                ["Auth:SigningKey"] = NewKey(),
                ["Auth:VisitorSigningKeys:Keys:0:Id"] = "new",
                ["Auth:VisitorSigningKeys:Keys:0:Value"] = NewKey(),
            }),
            new MutableClock(Now)));

        Assert.Contains("two answers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_FallsBackToAnEphemeralPerProcessKey()
    {
        // `3-06`'s original behaviour, kept: correct for the single-instance `dotnet run` loop, wrong
        // for more than one replica. The id is what makes that visible in a token rather than
        // something to infer from a 401.
        var ring = VisitorSigningKeyRing.FromConfiguration(
            Configuration(new Dictionary<string, string?>()), new MutableClock(Now));

        Assert.Equal(VisitorSigningKeyRing.EphemeralKeyId, ring.Signing.Key.KeyId);
        Assert.Single(ring.ValidationKeys());
    }

    // ------------------------------------------------------------------------------------------

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Base64Of(SecurityKey key) =>
        Convert.ToBase64String(((SymmetricSecurityKey)key).Key);

    private static VisitorSigningKeyEntry Entry(string id, string value, DateTimeOffset? retiredAt = null) =>
        new() { Id = id, Value = value, RetiredAt = retiredAt };

    private static VisitorSigningKeyRing Ring(
        DateTimeOffset now, TimeSpan retirementDelay, params VisitorSigningKeyEntry[] keys) =>
        Ring(new MutableClock(now), retirementDelay, keys);

    private static VisitorSigningKeyRing Ring(
        IClock clock, TimeSpan retirementDelay, params VisitorSigningKeyEntry[] keys) =>
        new(new VisitorSigningKeyOptions { RetirementDelay = retirementDelay, Keys = keys }, clock);

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
