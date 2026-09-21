namespace Ago.Chat.Domain;

/// <summary>
/// `26-03`/`adr/0179` §5: which push service a <see cref="OperatorDevice"/>'s token belongs to. A real
/// column from day one, even though <see cref="Fcm"/> is the only member ever written today - a
/// `token` column with no provider beside it would lie about what it holds the moment a second kind
/// of token exists, and that is a by-product of doing Android's own design cleanly, not iOS
/// preparation: no APNs adapter, no provider registry, no `IPushSenderFactory` exist anywhere in this
/// codebase yet, and none are added here. The trigger that reopens this enum is the first real iOS
/// device registration (`adr/0179`'s own remarks).
///
/// <para>Stored as the CLR member name via EF's default string conversion, the same shape
/// <see cref="ChannelKind"/> already uses for the identical reason: an ordinal makes reordering this
/// enum a silent data corruption, and this list is expected to grow by exactly one member.</para>
/// </summary>
public enum PushProvider
{
    Fcm,
}
