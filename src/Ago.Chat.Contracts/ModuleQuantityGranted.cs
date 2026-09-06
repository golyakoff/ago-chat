namespace Ago.Chat.Contracts;

/// <summary>
/// `22-07`/`adr/0093`: "site X's module K now has quantity Q" - the calendar add-on's own "N masters"
/// is the first real instance, but this event carries no word of that. Chat never learns what a
/// "calendar" or a "master" is (`ModuleKey`'s own remarks, restated for a second fact riding the same
/// opacity); a module interprets its own <see cref="Quantity"/>, chat only publishes that it changed.
///
/// <para><b>A snapshot of the <em>current</em> number, not a delta - the same shape
/// <see cref="RoleAssignmentsChanged"/> chose, for the identical reason.</b> Ordering is only
/// guaranteed per partition key (rule 6; this event's own publisher keys by <see cref="SiteId"/>), so
/// a consumer applying "+2"/"-1" facts out of order could land on the wrong number forever with no way
/// to notice. A snapshot re-applied twice under at-least-once delivery is naturally idempotent: the
/// second delivery sets the identical value the first one already set.</para>
///
/// <para><b>No display name, no price, no billing period - only what a module's own quota enforcement
/// needs.</b> The same no-body-crosses-the-broker discipline <see cref="RoleAssignmentsChanged"/>'s
/// own remarks state for a permission set.</para>
/// </summary>
public sealed record ModuleQuantityGranted(
    Guid SiteId,
    string ModuleKey,
    int Quantity,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
