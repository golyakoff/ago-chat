using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04` decision 5: the tenant declares they hold a lawful basis for their visitors' conversation
/// text reaching the LLM vendor. A command of its own, reaching its own handler and its own table -
/// see <see cref="AiProcessingBasisDeclaration"/> for why this is never a field on the acceptance or
/// on the enablement.
///
/// <para><b>Carries no free-text "basis" field, deliberately.</b> AGO does not verify the basis
/// (decision 5's own words), and a box AGO neither reads nor checks would invite a tenant to believe
/// it had been reviewed. What the record must answer is "who said this, and when" - the item's own
/// Done-when - not "what did they type".</para>
/// </summary>
public sealed record DeclareAiProcessingBasis(
    SiteId SiteId,
    OperatorId DeclaredBy,
    string? ClientIp = null,
    string? UserAgent = null);
