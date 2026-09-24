namespace Ago.Chat.Domain;

/// <summary>
/// `26-03`/`adr/0179` §5: which push service a <see cref="OperatorDevice"/>'s token belongs to. A real
/// column from day one - a `token` column with no provider beside it would lie about what it holds the
/// moment a second kind of token exists.
///
/// <para>`26-100`/`adr/0181`: that second kind now exists. <see cref="Fcm"/> joins <see cref="RuStore"/>
/// because push through RuStore's distributor model is killed by OEM memory management on de-Googled and
/// aggressive ROMs; FCM delivers through Google Play Services, which is always resident. FCM is the
/// primary transport where Play Services is present, RuStore the fallback where it is not - the client
/// chooses at registration time and writes the matching provider here, and
/// <c>NotifyOperatorDevicesHandler</c> routes each device's send to the sender for its provider
/// (`Ago.Chat.Application.Abstractions.IPushSenderResolver`). This is the concrete second member
/// `adr/0179` §5 named as the trigger that turns a one-entry guess into a real dispatch table.</para>
///
/// <para>Stored as the CLR member name via EF's default string conversion, the same shape
/// <see cref="ChannelKind"/> already uses for the identical reason: an ordinal makes reordering this
/// enum a silent data corruption. Members are therefore only ever appended, never reordered.</para>
/// </summary>
public enum PushProvider
{
    RuStore,

    /// <summary>`26-100`/`adr/0181`: Firebase Cloud Messaging (HTTP v1), the primary transport. Appended
    /// after <see cref="RuStore"/> - never inserted before it - because this enum is persisted by member
    /// name and any device row written before this change carries <see cref="RuStore"/>.</summary>
    Fcm,
}
