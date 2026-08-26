namespace Ago.Chat.Domain;

/// <summary>
/// `14-01`: which external messaging channel an <see cref="ChannelIdentity"/> was reached through -
/// AGO Inbox's own vocabulary (roadmap.md Stage 14), and the discriminator every concrete adapter
/// (`14-02` MAX, `14-03` SMS, `14-05`'s candidates) declares itself by.
///
/// <para><b>Why the widget is deliberately not a member here.</b> A widget visitor is identified by a
/// signed token this system issued to a browser (`realtime.md`, `adr/0048`), not by an identifier some
/// external provider owns, so it has no <see cref="ChannelIdentity"/> row and never will. Adding a
/// `Widget` member would invite exactly the wrong reading - that the widget is one channel among four
/// - when the real shape is "one built-in identity mechanism, plus N external ones that link *into*
/// it". See `adr/0055` and <see cref="ChannelIdentity"/>'s own remarks.</para>
///
/// <para>Stored as the CLR member name via EF's default string conversion (the same shape
/// <c>ConversationState</c> and <c>AttachmentState</c> already use), not as an ordinal - an ordinal
/// makes reordering this enum a silent data corruption, and this list will grow.</para>
/// </summary>
public enum ChannelKind
{
    Max,
    Sms,
    Telegram,
    WhatsApp,
}
