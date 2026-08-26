using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.Domain;

/// <summary>
/// `14-01`: the id an external provider gave one inbound message, and the one thing this system does
/// with it - derive the <c>ClientMessageId</c> that makes a redelivery a no-op.
///
/// <para><b>Why this is the idempotency key.</b> CLAUDE.md rule 5: inbound delivery from any external
/// channel is at-least-once. Every provider in Stage 14's list stamps each message with its own id and
/// repeats that id on a retry - that is what a retry <em>is</em>. Mapping it to
/// <c>Conversation.AddVisitorMessage</c>'s existing <c>clientMessageId</c> parameter means an inbound
/// redelivery takes the identical no-op path `5-07` already built for a widget message retried after a
/// flaky reconnect (<c>Conversation.AddMessage</c>'s own remarks): the original <see cref="Message"/>
/// comes back, no new <c>Sequence</c> is burned, no second <c>MessageAdded</c> is raised, and the
/// database-level unique index on <c>(conversation_id, client_message_id)</c> is still the backstop
/// for two processes racing the same redelivery. **No new deduplication mechanism was added for this
/// item** - that is the whole point, and it is what "a channel message becomes an ordinary AGO Chat
/// message the moment it is mapped" means in practice.</para>
///
/// <para><b>Why a hash rather than a lookup table.</b> A <c>channel_message_ids</c> ledger mapping
/// provider id to <c>ClientMessageId</c> would be a second idempotency store to keep consistent with
/// the first, and would have to be written in the same transaction as the message to be worth
/// anything. A pure function needs no storage, no transaction, and no reconciliation: the same
/// provider id always derives the same <see cref="Guid"/>, on any host, in any process, forever.</para>
///
/// <para><b>Why the channel is mixed in.</b> Two providers can legitimately issue the same id string
/// (a bare integer is common). Without <see cref="ChannelKind"/> in the digest, an SMS message and a
/// Telegram message that happen to share an id would collide - and a collision here is silent message
/// loss, since the second one would be swallowed as a duplicate. The unit separator (U+001F) between
/// the two fields is what stops <c>Sms|"12"</c> and <c>Sm|"s12"</c> hashing alike; it cannot occur in
/// an enum member name, so the split is unambiguous.</para>
/// </summary>
public readonly record struct ExternalMessageId
{
    public const int MaxLength = 256;

    /// <summary>Separates the two digest fields - see this type's own remarks on why concatenating
    /// them without one would be a real (if unlikely) collision.</summary>
    private const char FieldSeparator = '\u001F';

    public string Value { get; }

    public ExternalMessageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("External message id cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"External message id cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = trimmed;
    }

    /// <summary>
    /// The deterministic <c>ClientMessageId</c> this provider message maps to - a name-based UUID
    /// (RFC 9562 version 8, the custom-format version, with the standard variant bits) over
    /// <c><paramref name="kind"/> + U+001F + <see cref="Value"/></c>.
    ///
    /// <para>Version 8 rather than 5: version 5 is defined as SHA-1 over a namespace UUID, and this is
    /// SHA-256 over a string, so stamping it 5 would be claiming a construction it does not use.
    /// Version 8 is exactly the "vendor-specific, deterministic" slot RFC 9562 reserves.</para>
    ///
    /// <para><c>bigEndian: true</c> is not cosmetic. <see cref="Guid"/>'s default byte constructor
    /// reads the first three fields little-endian on this platform, which would put the version and
    /// variant bits somewhere other than where the canonical text form shows them - the value would
    /// still be deterministic, but it would not be a well-formed UUID, and anyone reading the column
    /// would be misled about what produced it.</para>
    ///
    /// <para><b>This is not, and must never become, an ordering input.</b> It decides identity only.
    /// Per-conversation order is the server-assigned <c>Message.Sequence</c> (CLAUDE.md rules 6 and
    /// 11); see `adr/0055`.</para>
    /// </summary>
    public Guid ToClientMessageId(ChannelKind kind)
    {
        var name = $"{kind}{FieldSeparator}{Value}";
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), digest);

        var guidBytes = digest[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x80); // version 8
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(guidBytes, bigEndian: true);
    }

    public override string ToString() => Value;
}
