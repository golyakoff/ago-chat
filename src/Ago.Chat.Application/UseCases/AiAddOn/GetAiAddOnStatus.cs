using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>`25-04`: everything the console's own AI screen needs in one read - what the tenant has
/// bought, what they have accepted, what they have declared, and whether it is on.</summary>
public sealed record GetAiAddOnStatus(SiteId SiteId, OperatorId RequestedBy);

/// <summary>
/// `25-04`: the four facts, kept apart on the wire exactly as they are kept apart in the schema.
///
/// <para><b><paramref name="AcceptedVersion"/>/<paramref name="AcceptedAt"/> and
/// <paramref name="DeclaredBy"/>/<paramref name="DeclaredAt"/> are two independent pairs</b>, either of
/// which may be present without the other - the wire-level half of decision 5's distinction. A console
/// renders two separate controls from them, and a reader of a support transcript can quote whichever
/// one is being asked about.</para>
/// </summary>
public sealed record AiAddOnStatus(
    bool Purchased,
    bool Enabled,
    DateTimeOffset? EffectiveFrom,
    string DocumentKey,
    string? CurrentVersion,
    string? CurrentTitle,
    string? CurrentBody,
    string? AcceptedVersion,
    DateTimeOffset? AcceptedAt,
    Guid? DeclaredBy,
    DateTimeOffset? DeclaredAt);
