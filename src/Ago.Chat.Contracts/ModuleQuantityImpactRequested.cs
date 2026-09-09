namespace Ago.Chat.Contracts;

/// <summary>
/// `23-88`/`adr/0093`: "how many of site X's module K's own countable things does candidate number Q
/// exceed" - the async question, riding the identical outbox mechanism
/// <see cref="ModuleQuantityGranted"/> already uses, never a live synchronous call
/// (`Domain.ModuleQuantityImpactPreview`'s own remarks state why in full). Chat never learns what a
/// module counts; it only asks whether a candidate number would exceed however much of it already
/// exists.
///
/// <para><b>Answered by a reply this repository does not yet define</b> - the module product that
/// answers this (the calendar add-on is the first) publishes its own answer over its own outbox, to a
/// topic <c>Ago.Chat.Worker</c>'s own consumer subscribes to by name (the identical "topic name is a
/// literal the receiving side cannot reference a type for" shape
/// <c>Ago.Calendar.Worker.ModuleQuantityGrantedConsumer</c>'s own remarks already establish for the
/// opposite direction). See this item's own report for exactly what that reply needs to carry and why
/// it is not built in this change.</para>
///
/// <para><b>A question, not a fact - so it carries no guarantee of an answer at all</b>, unlike
/// <see cref="ModuleQuantityGranted"/>'s snapshot (which a module is expected to apply). A module that
/// never answers leaves the asking site's own preview row pending forever, which is a real, visible
/// "still waiting" state a console shows honestly rather than a promise this event's own delivery
/// keeps.</para>
/// </summary>
/// <param name="RequestedQuantity">The candidate quantity the owner is considering, not the currently
/// granted one - answered against a number that may never actually be granted, since the owner is
/// still deciding.</param>
public sealed record ModuleQuantityImpactRequested(
    Guid SiteId,
    string ModuleKey,
    int RequestedQuantity,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
