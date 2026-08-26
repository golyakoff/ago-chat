namespace Ago.Chat.Domain;

/// <summary>
/// `14-01`: the raw identifier an external channel uses for one correspondent - a phone number for
/// SMS, a chat id for MAX or Telegram - paired with a <see cref="ChannelKind"/> and a
/// <c>SiteId</c> to form the lookup key of a <see cref="ChannelIdentity"/>.
///
/// <para><b>What this type validates, and what it deliberately refuses to.</b> It enforces only what
/// is true of every channel: non-empty, trimmed, bounded. It does <em>not</em> canonicalise per
/// channel - no E.164 rewriting for <see cref="ChannelKind.Sms"/>, no numeric parsing for
/// <see cref="ChannelKind.Telegram"/>. Canonicalisation is a fact about one provider's own format
/// rules, which is precisely the knowledge `adr/0006`'s "largest common denominator that does not
/// lie" reasoning says must stay below the Infrastructure boundary. A Domain type that guessed at
/// E.164 would be wrong for the first provider that hands us a national-format number, and being
/// wrong here means two rows for one human. **The concrete adapter canonicalises before it
/// constructs this**, and `14-02`/`14-03` are where that responsibility lands.</para>
///
/// <para><b>Case is preserved, deliberately.</b> Lower-casing would be safe for a phone number and
/// unsafe for an opaque provider-issued id, where two identifiers differing only in case can be two
/// different people. The uniqueness guarantee (`ChannelIdentityConfiguration`'s index) is therefore
/// case-sensitive, and an adapter whose provider is case-insensitive must fold case itself - the
/// same division of labour as canonicalisation above.</para>
/// </summary>
public readonly record struct ExternalChannelAddress
{
    // Generous enough for any provider id or phone number seen in the wild, small enough that this
    // column can carry a btree index without special handling. No product requirement pins it.
    public const int MaxLength = 256;

    public string Value { get; }

    public ExternalChannelAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("External channel address cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"External channel address cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = trimmed;
    }

    public override string ToString() => Value;
}
