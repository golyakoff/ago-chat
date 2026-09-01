using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListModuleTaskChannelPriorityList;

/// <summary>What a console panel needs to render one row - the underlying <see cref="ChannelIdentity"/>'s
/// own kind/address (joined in by the handler, never duplicated in storage - see
/// <see cref="Domain.ModuleTaskChannelPreference"/>'s own remarks on why this is a live reference, not a
/// snapshot), plus this entry's own priority and whether the identity behind it is still active. An
/// inactive row is still returned (not silently dropped) so a console can show "this channel was
/// unlinked" rather than a list that mysteriously shrank.</summary>
public sealed record ModuleTaskChannelPreferenceSummary(
    Guid ChannelIdentityId, ChannelKind Kind, string Address, int Priority, DateTimeOffset AddedAt, bool IsActive);
