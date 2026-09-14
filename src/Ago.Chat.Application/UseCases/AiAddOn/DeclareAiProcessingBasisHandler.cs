using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04` decision 5. Writes one <see cref="AiProcessingBasisDeclaration"/> and nothing else - it does
/// not enable the add-on, does not record an acceptance, and does not check whether the agreement has
/// been accepted.
///
/// <para><b>The independence is the point, not an omission.</b> If declaring implied accepting (or the
/// reverse), the two facts would always carry the same timestamp and the same author, and a later
/// reader could no longer tell whether the tenant had actually done both things or whether the system
/// had inferred one from the other. <c>EnableAiAddOnHandler</c> is the only place that requires both,
/// and it requires them as two separate lookups with two separate refusals.</para>
/// </summary>
public sealed class DeclareAiProcessingBasisHandler(
    IPermissionChecker permissions,
    IAiProcessingBasisDeclarationRepository declarations,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RecordedAiProcessingBasisDeclaration>> HandleAsync(
        DeclareAiProcessingBasis command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.DeclaredBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return AiAddOnErrors.Forbidden("Operator does not have permission to configure this site.");
        }

        var now = clock.UtcNow;
        AiProcessingBasisDeclaration declaration;
        try
        {
            declaration = AiProcessingBasisDeclaration.Declare(
                new AiProcessingBasisDeclarationId(idGenerator.NewId(now)), command.SiteId, command.DeclaredBy, now,
                command.ClientIp, command.UserAgent);
        }
        catch (ArgumentException ex)
        {
            return AiAddOnErrors.Forbidden(ex.Message);
        }

        await declarations.SaveAsync(declaration, cancellationToken);

        return new RecordedAiProcessingBasisDeclaration(
            declaration.Id.Value, declaration.SiteId.Value, declaration.DeclaredBy.Value, declaration.DeclaredAt);
    }
}

/// <summary>What a caller gets back - the four facts that make the declaration citable later.</summary>
public sealed record RecordedAiProcessingBasisDeclaration(Guid Id, Guid SiteId, Guid DeclaredBy, DateTimeOffset DeclaredAt);
