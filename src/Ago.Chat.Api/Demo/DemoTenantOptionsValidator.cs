using Ago.Chat.Application.UseCases.MintDemoTenant;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Demo;

/// <summary>
/// `23-45`: a cross-property rule no data annotation can express - if minting is on, the shared
/// account whose password the demo pages publish must be named.
///
/// <para><b>Why it lives in the host rather than beside the options class it validates.</b>
/// <c>IValidateOptions&lt;T&gt;</c> comes from <c>Microsoft.Extensions.Options</c>, and
/// <c>Ago.Chat.Application</c> does not reference it - deliberately, which is why every handler in this
/// project takes a bound options <i>value</i> (registered as a singleton by <c>Program.cs</c>) rather
/// than an <c>IOptions&lt;T&gt;</c>. Binding configuration and validating it are hosting concerns, and
/// the host is the only layer allowed to know about them (CLAUDE.md rule 1). The alternative - putting
/// this next to <c>DemoTenantOptions</c> - was tried first and the compiler refused it, which is the
/// dependency rule doing its job rather than a preference.</para>
///
/// <para><b>What it protects.</b> `23-45` inverts the direction the console's standing demo band fails
/// in. Before it, a route that forgot to say who the reader was showed the <i>stricter</i> text; now the
/// band appears only when the caller's site is named here, so an empty list on a demo deployment removes
/// the warning from the one account that needs it - the person signed in with credentials anybody can
/// read off a web page. That failure would be invisible: a missing sentence on a console that otherwise
/// works perfectly. A refusal to start is loud, which is the entire reason this class exists.</para>
///
/// <para><b>Registered in this host only.</b> <c>Ago.Chat.Worker</c> binds the same options for
/// <c>DemoTenantExpiryJob</c>, which reads the lifetime and nothing else. This rule is about what the
/// API tells the console, so demanding the list from a process that never answers
/// <c>/api/v1/operators/me</c> would be a validation failing for a reason untrue of the thing failing.</para>
/// </summary>
public sealed class DemoTenantOptionsValidator : IValidateOptions<DemoTenantOptions>
{
    public ValidateOptionsResult Validate(string? name, DemoTenantOptions options)
    {
        if (options.Enabled && options.PublishedCredentialSitePublicKeys.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                $"{DemoTenantOptions.SectionName}:{nameof(DemoTenantOptions.PublishedCredentialSitePublicKeys)} must name at least "
                + "one site when demo-tenant minting is enabled. The public demo console's standing warning is shown only to the "
                + "accounts named here, so an empty list removes it from the shared account whose password is published.");
        }

        return ValidateOptionsResult.Success;
    }
}
