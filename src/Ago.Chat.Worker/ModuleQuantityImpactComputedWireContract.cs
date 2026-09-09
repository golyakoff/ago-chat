namespace Ago.Chat.Worker;

/// <summary>
/// `23-88`: this product's own copy of the reply wire shape whichever module answers
/// `Ago.Chat.Contracts.ModuleQuantityImpactRequested` is expected to publish - independently declared
/// on this side too, the same "no shared Contracts assembly across the product boundary" reason
/// `Ago.Calendar.Worker.ModuleQuantityGrantedWireContract`'s own remarks give for the opposite
/// direction (`adr/0027`).
///
/// <para><b>Not yet published by anything - this is the specification this item's own report states,
/// not a contract already answered on a live deployment.</b> No module product publishes to the
/// topic <see cref="ModuleQuantityImpactComputedConsumer"/> subscribes to
/// (<c>"ModuleQuantityImpactComputed"</c>) as of this change; this consumer and this wire shape exist
/// so the chat-side half of the round trip is complete and ready the day a module product's own
/// answering half ships, the same "ships as a mechanism with no caller yet" posture
/// <c>GrantModuleQuantity</c> itself once had before `23-66` gave it an owner-facing endpoint.</para>
///
/// <para><see cref="AffectedItemDisplayNames"/> is opaque - see
/// <c>Domain.ModuleQuantityImpactPreview</c>'s own remarks for why this repository never inspects
/// what the strings name.</para>
/// </summary>
internal sealed record ModuleQuantityImpactComputedWireContract(
    Guid SiteId,
    string ModuleKey,
    int RequestedQuantity,
    int AffectedCount,
    IReadOnlyList<string> AffectedItemDisplayNames,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
