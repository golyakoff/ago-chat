using System.Net;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-06`/`adr/0034`: the realm's login-security policy, proven against a real Keycloak rather than
/// read off the import file. Two different questions, deliberately answered two different ways.
///
/// <see cref="RealmCarriesTheChosenLoginSecuritySettings"/> reads the realm back over the admin API,
/// because the import file and the running realm are not the same thing: `--import-realm` is
/// skip-if-exists, so a settings change that never reached a live realm is a failure mode this
/// project has already hit for real (`ago-deploy/k8s/base/kustomization.yaml`'s own note). A test that
/// asserted against the JSON would restate the file, not verify it.
///
/// <see cref="FailedLogins_LockTheAccountOut_SoTheCorrectPasswordStopsWorking"/> is the behavioural
/// half - `17-06`'s own Done-when asks for "a real failed-login sequence that locks out", which no
/// amount of configuration reading can stand in for. Note what it asserts and what it does not: that
/// the *correct* password stops working, and that Keycloak's own attack-detection view reports the
/// account disabled. It does not assert an exact failure count, because two independent thresholds
/// can fire here - `failureFactor` (10) and the quick-login guard
/// (`quickLoginCheckMilliSeconds`/`minimumQuickLoginWaitSeconds`, which trips on failures arriving
/// faster than a human types, exactly what a loop of HTTP calls looks like). Which one fires first is
/// a timing detail; that one of them fires is the property worth locking down. The chosen numbers
/// themselves are covered by the settings test above.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class RealmLoginSecurityTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task RealmCarriesTheChosenLoginSecuritySettings()
    {
        var realm = await fixture.GetRealmRepresentationAsync();

        Assert.True(realm.GetProperty("bruteForceProtected").GetBoolean());
        // Temporary lockout, never permanent: on a realm anyone can self-register into (`adr/0028`),
        // permanent lockout hands an attacker a denial-of-service against any username they can guess.
        Assert.False(realm.GetProperty("permanentLockout").GetBoolean());
        Assert.Equal(10, realm.GetProperty("failureFactor").GetInt32());
        Assert.Equal(60, realm.GetProperty("waitIncrementSeconds").GetInt32());
        Assert.Equal(900, realm.GetProperty("maxFailureWaitSeconds").GetInt32());

        var passwordPolicy = realm.GetProperty("passwordPolicy").GetString();
        Assert.Contains("length(12)", passwordPolicy);
        Assert.Contains("notUsername", passwordPolicy);
        Assert.Contains("notEmail", passwordPolicy);

        // The second-factor decision (`adr/0034`): TOTP parameters chosen rather than inherited, so
        // that enabling a second factor later is a per-user or per-realm switch rather than another
        // set of defaults nobody looked at - but no required action forces enrolment today.
        Assert.Equal("totp", realm.GetProperty("otpPolicyType").GetString());
        Assert.Equal(6, realm.GetProperty("otpPolicyDigits").GetInt32());
        Assert.Equal(30, realm.GetProperty("otpPolicyPeriod").GetInt32());
    }

    [Fact]
    public async Task FailedLogins_LockTheAccountOut_SoTheCorrectPasswordStopsWorking()
    {
        // Start from a known state: an earlier run against a reused container may have left this
        // user locked, which would make the "correct password works first" premise below false.
        await fixture.ClearBruteForceAttemptsAsync();

        var (beforeStatus, _) = await fixture.TryPasswordGrantAsync(
            OperatorOidcFixture.LockoutTargetUsername, OperatorOidcFixture.LockoutTargetPassword);
        Assert.Equal(HttpStatusCode.OK, beforeStatus);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var (status, _) = await fixture.TryPasswordGrantAsync(
                OperatorOidcFixture.LockoutTargetUsername, "definitely-not-the-password");
            Assert.NotEqual(HttpStatusCode.OK, status);
        }

        // The whole proof: the credentials that worked a moment ago no longer do. Keycloak answers a
        // locked account and a wrong password identically (`invalid_grant`, deliberately - no user
        // enumeration), so the status/body cannot distinguish them; only "correct password, still
        // rejected" can.
        var (afterStatus, _) = await fixture.TryPasswordGrantAsync(
            OperatorOidcFixture.LockoutTargetUsername, OperatorOidcFixture.LockoutTargetPassword);
        Assert.NotEqual(HttpStatusCode.OK, afterStatus);

        var userId = await fixture.GetUserIdAsync(OperatorOidcFixture.LockoutTargetUsername);
        var bruteForce = await fixture.GetBruteForceStatusAsync(userId);
        Assert.True(bruteForce.GetProperty("disabled").GetBoolean());
        Assert.True(bruteForce.GetProperty("numFailures").GetInt32() > 0);

        // Leave the realm as this test found it - the lockout is temporary anyway, but a container
        // reused across a re-run should not carry one test's deliberate damage into the next.
        await fixture.ClearBruteForceAttemptsAsync();
    }
}
