using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetModuleQuantityImpactPreviewAsOwner;

/// <summary>
/// `23-88`: reads back whatever <see cref="IModuleQuantityImpactPreviewStore"/> currently holds for
/// one site's module - three states a console must render distinctly, never collapsed into one
/// "loading" spinner: <b>never asked</b> (no row at all - the owner has not previewed anything for
/// this module since it was last granted), <b>asked, not yet answered</b> (a row exists,
/// <c>AnsweredAt</c> is <see langword="null"/> - the module has not replied, and may never, if it is
/// unreachable or does not implement this reply at all), and <b>answered</b> (a real count and,
/// possibly, some opaque display names). <see cref="GetModuleQuantityImpactPreviewResult"/> carries
/// all three without inventing a fourth "error" state this read never actually produces - a missing
/// or unanswered row is not a fault, it is an honest fact about where the async round trip currently
/// stands.
/// </summary>
public sealed class GetModuleQuantityImpactPreviewAsOwnerHandler(IModuleQuantityImpactPreviewStore previews)
{
    public async Task<Result<GetModuleQuantityImpactPreviewResult>> HandleAsync(
        GetModuleQuantityImpactPreviewAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        var preview = await previews.TryGetAsync(command.SiteId, moduleKey, cancellationToken);
        if (preview is null)
        {
            return new GetModuleQuantityImpactPreviewResult(
                Requested: false, RequestedQuantity: null, Answered: false, AffectedCount: null, AffectedItemDisplayNames: []);
        }

        return new GetModuleQuantityImpactPreviewResult(
            Requested: true,
            RequestedQuantity: preview.RequestedQuantity,
            Answered: preview.AnsweredAt is not null,
            AffectedCount: preview.AffectedCount,
            AffectedItemDisplayNames: preview.AffectedItemDisplayNames);
    }
}

/// <param name="Requested">Whether anything has ever been asked about this site's module.</param>
/// <param name="RequestedQuantity">The candidate number the current (possibly still-pending) question
/// is about - <see langword="null"/> only when <paramref name="Requested"/> is <see langword="false"/>.</param>
/// <param name="Answered">Whether the module has replied to the question named by
/// <paramref name="RequestedQuantity"/> yet.</param>
public sealed record GetModuleQuantityImpactPreviewResult(
    bool Requested, int? RequestedQuantity, bool Answered, int? AffectedCount, IReadOnlyList<string> AffectedItemDisplayNames);
