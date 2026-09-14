namespace Ago.Chat.Domain;

/// <summary>
/// `25-04`: the one place the AI add-on's own agreement document is named. A `24-02`
/// <see cref="Document"/> key, never a <see cref="ModuleKey"/> - the two namespaces are unrelated, and
/// this literal is safe here for exactly the reason a <see cref="ModuleKey"/> literal would not be:
/// the agreement is <b>AGO's own agreement with its tenant</b> (`25-04` decision 4), not a fact about
/// some other product this assembly must stay ignorant of.
///
/// <para><b>A constant rather than configuration</b>, unlike <c>AiAddOnOptions.ModuleKey</c> (which is
/// a module-registry key and therefore deployment data, `IBillingOptionEntitlementProvider`'s own
/// remarks). Which document a tenant must accept to turn our own feature on is not something a
/// deployment may redefine: a deployment that pointed this at a different key would be silently
/// accepting a different agreement than the one this codebase's own refusal logic was reasoned about.
/// The <em>text</em> is still free to change - `24-02` versions it, and
/// <c>EnableAiAddOnHandler</c> refuses anything but the current version.</para>
/// </summary>
public static class AiAddOnAgreement
{
    public const string DocumentKey = "ai-processing-addendum";
}
