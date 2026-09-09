using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-88`'s <see cref="IModuleQuantityImpactPreviewStore"/> adapter - the identical one-
/// transaction-per-write shape <see cref="ModuleQuantityGrantStore"/> already establishes for its own
/// sibling, widened by the outbox row on <see cref="RequestAsync"/> only: <see cref="AnswerAsync"/>
/// records the module's own reply and never publishes anything of its own, so it has no outbox row to
/// stage.</summary>
public sealed class ModuleQuantityImpactPreviewStore(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator)
    : IModuleQuantityImpactPreviewStore
{
    public async Task<ModuleQuantityImpactPreview?> TryGetAsync(
        SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        await db.ModuleQuantityImpactPreviews.AsNoTracking()
            .FirstOrDefaultAsync(p => p.SiteId == siteId && p.ModuleKey == moduleKey, cancellationToken);

    public async Task RequestAsync(
        SiteId siteId, ModuleKey moduleKey, int requestedQuantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var preview = await db.ModuleQuantityImpactPreviews
            .FirstOrDefaultAsync(p => p.SiteId == siteId && p.ModuleKey == moduleKey, cancellationToken);

        if (preview is null)
        {
            preview = ModuleQuantityImpactPreview.Request(siteId, moduleKey, requestedQuantity, now);
            db.ModuleQuantityImpactPreviews.Add(preview);
        }
        else
        {
            // `23-88`: a fresh question supersedes whatever this row held before, answered or not -
            // ModuleQuantityImpactPreview.Reset's own remarks.
            preview.Reset(requestedQuantity, now);
        }

        outbox.Enqueue(ModuleQuantityImpactRequestedMapper.ToEnvelope(siteId.Value, moduleKey.Value, requestedQuantity, now, idGenerator));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AnswerAsync(
        SiteId siteId, ModuleKey moduleKey, int answeredQuantity, int affectedCount,
        IReadOnlyList<string> affectedItemDisplayNames, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var preview = await db.ModuleQuantityImpactPreviews
            .FirstOrDefaultAsync(p => p.SiteId == siteId && p.ModuleKey == moduleKey, cancellationToken);

        // `23-88`/IModuleQuantityImpactPreviewStore's own remarks: a late or stray answer is a silent
        // no-op, never an error - it is not this consumer's row to write into any more (or ever was).
        if (preview is null || preview.RequestedQuantity != answeredQuantity)
        {
            return;
        }

        preview.Answer(affectedCount, affectedItemDisplayNames, now);
        await db.SaveChangesAsync(cancellationToken);
    }
}
