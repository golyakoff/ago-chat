using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-85`'s own point 3 - "find out why the Keycloak-redirect path didn't carry the code for this
/// real walkthrough." <c>CallbackPage.tsx</c> already reads `?inviteCode=...` off `window.location.search`
/// and `CreateOperatorInviteHandler` already builds `redirect_uri` as
/// `{ConsoleBaseUrl}/callback?inviteCode={code}` (`?client_id=...&redirect_uri=...` on Keycloak's own
/// `execute-actions-email` Admin API call, per `OperatorInviteEmailProvisioner`) - so the question is
/// entirely on Keycloak's own side: does the query string appended to `redirect_uri` actually survive
/// the required-actions completion (`UPDATE_PASSWORD` then `UPDATE_PROFILE`) all the way to the final
/// authorization-code redirect back to the client?
///
/// <para><b>Why this drives the ordinary login flow, not `execute-actions-email` itself.</b> Reproducing
/// the exact email-link/action-token mechanism end to end would need a live SMTP relay Keycloak could
/// actually send to (this suite's own realm carries none) plus parsing the action-token link out of a
/// sent message - a materially larger build this item's own time did not allow. What this class drives
/// instead is the *standard* Authorization Code flow against a user Keycloak requires to complete
/// `UPDATE_PASSWORD` then `UPDATE_PROFILE` before it will ever issue a code - the identical requirement
/// an action-token-driven invitee faces, and (so far as this investigation could determine without
/// reading Keycloak's own server source) the same final leg of code: once required actions are
/// satisfied, `AuthenticationManager`'s own "finish the browser flow and redirect to the client" step
/// runs regardless of which door the browser walked in through. This is stated as a reasoned inference,
/// not a certainty - see this item's own worker report for the honest confidence level.</para>
///
/// <para><b>Method: raw HTTP, not a headless browser.</b> Keycloak's own login/required-action pages
/// are ordinary HTML forms with no client-side JavaScript required to submit them - so a plain
/// <see cref="HttpClient"/> with cookies enabled and automatic redirects turned off, parsing each form's
/// own `action` URL and field names by regex, reproduces exactly what a browser would send without
/// needing one.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OperatorInviteRedirectQueryStringInvestigationTests(OperatorOidcFixture fixture)
{
    private const string ClientId = OperatorOidcFixture.ClientId;

    /// <summary>
    /// `25-85`'s own point 3, actually found rather than merely searched for: a real Keycloak, driven
    /// through the exact `requiredActions` <c>OperatorInviteEmailProvisioner.CreateOrFindUserAsync</c>
    /// gives an operator-invite user, diverts to a THIRD required action - `VERIFY_EMAIL` - neither
    /// `CreateOperatorInviteHandler` nor `OperatorInviteEmailProvisioner` ever asked for, because this
    /// realm's own `verifyEmail: true` setting (`keycloak-realm-import.json`, both this suite's test
    /// realm and `ago-deploy`'s real one) makes Keycloak's browser flow append `VERIFY_EMAIL`
    /// unconditionally to *any* authentication by a user whose `emailVerified` is `false` - which every
    /// operator-invite user's is, by that same provisioner's own explicit `emailVerified = false`. The
    /// OAuth redirect back to `redirect_uri` (carrying `?inviteCode=...`) never fires, because the
    /// browser flow does not finish - it stops on a "verify your email" holding page instead, which
    /// sends a *second*, unrelated email through Keycloak's own generic verify-email template (not
    /// `execute-actions-email`), with no code and no `redirect_uri` context carried over the same way.
    /// This item's own worker report has the full reading of what this means and does not mean.
    /// </summary>
    [Fact]
    public async Task RequiredActionsCompletion_ForAnUnverifiedEmailUser_DivertsToEmailVerificationBeforeTheClientRedirect()
    {
        var username = $"invite-flow-{Guid.NewGuid():N}";
        const string temporaryPassword = "TempPassw0rd!123";
        await fixture.CreateUserPendingRequiredActionsAsync(username, temporaryPassword);

        const string inviteCode = "invite_investigation-code-123";
        var redirectUri = $"https://console.example.test/callback?inviteCode={Uri.EscapeDataString(inviteCode)}";

        using var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
            AllowAutoRedirect = false,
        };
        using var http = new HttpClient(handler);

        // Step 1: the ordinary authorization request a browser makes first - `execute-actions-email`'s
        // own action-token click ultimately lands the browser here too, carrying the identical
        // `client_id`/`redirect_uri` pair `OperatorInviteEmailProvisioner.SendActionsEmailAsync` gives
        // Keycloak.
        var authorizeUrl = $"{fixture.KeycloakAuthority}/protocol/openid-connect/auth"
            + $"?client_id={Uri.EscapeDataString(ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code&scope=openid&state=investigation-state";

        var loginPage = await http.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        Diag($"[1] GET auth -> {(int)loginPage.StatusCode} title={PageTitle(loginHtml)}");

        // Step 2: authenticate as the invitee - the login form's own action URL and hidden fields,
        // submitted with the real username/password. Found live, against this real Keycloak: the login
        // POST does not 302 to the next step's own URL - it renders that step's HTML directly in the
        // `200` response body (an internal server-side forward, invisible to a real browser only
        // because a browser does not distinguish the two), so `NextStepHtmlAsync` below reads the body
        // directly rather than assuming a `Location` header exists.
        var (loginAction, loginFields) = ParseForm(loginHtml);
        loginFields["username"] = username;
        loginFields["password"] = temporaryPassword;
        Diag($"[2] POST login action={loginAction} fields={string.Join(",", loginFields.Keys)}");
        var afterLogin = await PostFormAsync(http, loginAction, loginFields);
        Diag($"[2] -> {(int)afterLogin.StatusCode} location={afterLogin.Headers.Location}");

        // Step 3: `UPDATE_PASSWORD` - the first required action this user was created with.
        var updatePasswordHtml = await NextStepHtmlAsync(http, afterLogin);
        Diag($"[3] step html title={PageTitle(updatePasswordHtml)}");
        var (updatePasswordAction, updatePasswordFields) = ParseForm(updatePasswordHtml);
        const string newPassword = "BrandNewPassw0rd!456";
        updatePasswordFields["password-new"] = newPassword;
        updatePasswordFields["password-confirm"] = newPassword;
        Diag($"[3] POST update-password action={updatePasswordAction} fields={string.Join(",", updatePasswordFields.Keys)}");
        var afterUpdatePassword = await PostFormAsync(http, updatePasswordAction, updatePasswordFields);
        Diag($"[3] -> {(int)afterUpdatePassword.StatusCode} location={afterUpdatePassword.Headers.Location}");

        // Step 4: `UPDATE_PROFILE` - the second required action.
        var updateProfileHtml = await NextStepHtmlAsync(http, afterUpdatePassword);
        Diag($"[4] step html title={PageTitle(updateProfileHtml)}");
        var (updateProfileAction, updateProfileFields) = ParseForm(updateProfileHtml);
        updateProfileFields["firstName"] = "Invitee";
        updateProfileFields["lastName"] = "Person";
        Diag($"[4] POST update-profile action={updateProfileAction} fields={string.Join(",", updateProfileFields.Keys)}");
        var afterUpdateProfile = await PostFormAsync(http, updateProfileAction, updateProfileFields);
        Diag($"[4] -> {(int)afterUpdateProfile.StatusCode} location={afterUpdateProfile.Headers.Location}");

        // Step 5: the final leg - required actions satisfied, expected to redirect back to the client.
        // Found live, against this real Keycloak: it does not.
        var outcome = await FollowToClientRedirectAsync(http, afterUpdateProfile);

        // The load-bearing finding, asserted rather than only logged: the standard authorization-code
        // flow, for a user provisioned exactly the way `OperatorInviteEmailProvisioner` provisions a
        // real invitee, never reaches the client redirect at all - it diverts to email verification
        // instead. A change to this realm's `verifyEmail` setting, or to that provisioner's own
        // `emailVerified` value, would need this test revisited - that is the point of a real assertion
        // here rather than only a logged observation.
        Assert.Null(outcome.RedirectLocation);
        Assert.NotNull(outcome.DivertedToHtml);
        Assert.Contains("verify your email", outcome.DivertedToHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(username + "@example.test", outcome.DivertedToHtml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the first `&lt;form&gt;` on the page: its own `action` URL and every `&lt;input&gt;`
    /// name/value pair (hidden fields included), the same information a browser reads before submitting
    /// it. Keycloak's own default login theme renders exactly one form per required-action page, so the
    /// first match is always the right one.</summary>
    private static (string Action, Dictionary<string, string> Fields) ParseForm(string html)
    {
        var formMatch = Regex.Match(html, "<form[^>]*\\baction=\"([^\"]*)\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(formMatch.Success, $"No <form> found in the page. First 500 chars: {Truncate(html)}");

        var action = HttpUtility.HtmlDecode(formMatch.Groups[1].Value);
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(formMatch.Groups[2].Value, "<input\\b[^>]*>"))
        {
            var nameMatch = Regex.Match(input.Value, "name=\"([^\"]*)\"");
            if (!nameMatch.Success)
            {
                continue;
            }

            var valueMatch = Regex.Match(input.Value, "value=\"([^\"]*)\"");
            fields[nameMatch.Groups[1].Value] = valueMatch.Success ? HttpUtility.HtmlDecode(valueMatch.Groups[1].Value) : string.Empty;
        }

        return (action, fields);
    }

    /// <summary>The non-asserting sibling of <see cref="ParseForm"/> - used only where a `&lt;form&gt;`
    /// being absent is itself a real, informative outcome (the final leg's own landing page might be a
    /// plain confirmation with nothing left to submit) rather than a parsing bug.</summary>
    private static bool TryParseForm(string html, out string action, out Dictionary<string, string> fields)
    {
        var formMatch = Regex.Match(html, "<form[^>]*\\baction=\"([^\"]*)\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
        if (!formMatch.Success)
        {
            action = string.Empty;
            fields = new Dictionary<string, string>();
            return false;
        }

        (action, fields) = ParseForm(html);
        return true;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient http, string action, Dictionary<string, string> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        return await http.PostAsync(action, content);
    }

    /// <summary>The next required-action step's own HTML - found live, against this real Keycloak, to
    /// arrive as a direct `200` body on the very response that submitted the previous step's form (an
    /// internal server-side forward), not as a `302 Location` a browser would visibly follow. Handles
    /// both shapes anyway, in case a differently-configured realm (or a later Keycloak version) really
    /// does redirect instead - this investigation's own job is to observe what actually happens, not to
    /// assume one specific shape.</summary>
    private static async Task<string> NextStepHtmlAsync(HttpClient http, HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return await response.Content.ReadAsStringAsync();
        }

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.Found,
            $"Expected either a 200 body or a redirect between required-action steps, got {(int)response.StatusCode}. " +
            $"Body: {Truncate(await response.Content.ReadAsStringAsync())}");
        var location = response.Headers.Location ?? throw new InvalidOperationException("Redirect carried no Location header.");
        var absolute = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
        var next = await http.GetAsync(absolute);
        return await next.Content.ReadAsStringAsync();
    }

    /// <summary>What the final leg of the flow actually did - either it reached the client
    /// (<paramref name="RedirectLocation"/> set) or it diverted somewhere else first
    /// (<paramref name="DivertedToHtml"/> set, the page it stopped on).</summary>
    private sealed record FinalLegOutcome(Uri? RedirectLocation, string? DivertedToHtml);

    /// <summary>Follows redirects (there may be more than one - an interstitial page between the last
    /// required action and the client redirect is a real, observed Keycloak behaviour, not assumed
    /// away) until one lands outside this Keycloak realm entirely (the client's own `redirect_uri`,
    /// this investigation's actual subject) or the flow stops somewhere else first - captured rather
    /// than discarded, since that "somewhere else" turned out to be this item's own real finding.</summary>
    private static async Task<FinalLegOutcome> FollowToClientRedirectAsync(HttpClient http, HttpResponseMessage response)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // The identical server-side-forward shape `NextStepHtmlAsync` already found between
                // required-action steps - inspected here (not just consumed) because this investigation's
                // own open question is exactly what this particular forwarded page turns out to be.
                var html = await response.Content.ReadAsStringAsync();
                if (TryParseForm(html, out var action, out var fields))
                {
                    Diag($"[5] hop {hop}: 200 body is a FORM. action={action} fields={string.Join(",", fields.Keys)} title={PageTitle(html)}");
                    response = await PostFormAsync(http, action, fields);
                    continue;
                }

                Diag($"[5] hop {hop}: 200 body, NO form. title={PageTitle(html)}");
                return new FinalLegOutcome(RedirectLocation: null, DivertedToHtml: html);
            }

            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.Found))
            {
                Assert.Fail(
                    $"Expected a redirect toward the client at hop {hop}, got {(int)response.StatusCode}. " +
                    $"Body: {Truncate(await response.Content.ReadAsStringAsync())}");
            }

            var location = response.Headers.Location!;
            Diag($"[5] hop {hop}: redirect -> {location}");
            if (location.IsAbsoluteUri && location.Host == "console.example.test")
            {
                return new FinalLegOutcome(RedirectLocation: location, DivertedToHtml: null);
            }

            var next = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
            response = await http.GetAsync(next);
        }

        return new FinalLegOutcome(RedirectLocation: null, DivertedToHtml: null);
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    private static string PageTitle(string html)
    {
        var match = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "(no <title>)";
    }

    /// <summary>Diagnostic-only trace of what each step actually returned - a human reading a failure
    /// or debugging a future change to this flow reads this, `dotnet test -l "console;verbosity=detailed"`,
    /// same as any other test's own stdout; none of this test's own assertions depend on it.</summary>
    private static void Diag(string line) => Console.WriteLine(line);
}
