namespace Ago.Chat.Domain;

/// <summary>
/// `23-19`: the only two outcomes <see cref="ChannelDelivery"/> ever records - the two terminal branches
/// of <c>Application.Abstractions.ChannelSendOutcome</c> that <c>DeliverChannelMessageHandler</c> turns into a
/// row. A transient fault never reaches here (it throws, per <c>Application.Abstractions.IInboundChannelAdapter</c>'s
/// own contract), and neither does <c>DeliverChannelMessageOutcome.NoLinkedChannel</c> /
/// <c>NoAdapterRegistered</c> - both are "nothing was attempted," not a delivery outcome, and this item's
/// own Done-when is explicit that a no-linked-channel conversation "writes nothing at all."
///
/// <para>Stored as the CLR member name (the same <c>HasConversion&lt;string&gt;()</c> shape
/// <see cref="ChannelKind"/>/<see cref="ConversationState"/> already use), not as an ordinal.</para>
/// </summary>
public enum ChannelDeliveryStatus
{
    Delivered,
    Refused,
}
