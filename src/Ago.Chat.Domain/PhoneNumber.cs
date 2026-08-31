using System.Text;

namespace Ago.Chat.Domain;

/// <summary>
/// `14-15`: the canonical form a phone number takes everywhere this feature touches one - the rate
/// limit key, <see cref="PendingPhoneVerification.Phone"/>, and the <see cref="ExternalChannelAddress"/>
/// eventually handed to <see cref="ChannelIdentity.Link"/>. Normalised on construction, to E.164 shape -
/// a leading <c>+</c>, no leading zero, 8 to 15 digits - so <c>+7 (999) 123-45-67</c> and
/// <c>+79991234567</c> collapse to one value rather than becoming two different rate-limit buckets or two
/// different <see cref="ChannelIdentity"/> rows for the same human (this item's own "where this is most
/// likely to go wrong" note 3: normalise once, at the entry point, use that one canonical string
/// downstream).
///
/// <para><b>Why this is a Domain type here, unlike <see cref="ExternalChannelAddress"/>'s own deliberate
/// refusal to canonicalise.</b> That type's own remarks are explicit that E.164 rewriting is "precisely
/// the knowledge that must stay below the Infrastructure boundary" - true for <em>that</em> type, because
/// it is the address of a real message-passing provider (Telegram, MAX, Avito) whose own id format is a
/// fact about that specific provider's API, not about telephony. This type validates a materially
/// different claim: "is this string shaped like an international phone number at all", the same universal
/// ITU-T E.164 rule every phone network agrees on, independent of which SMS/voice gateway eventually
/// dials it - the same reasoning `Ago.Calendar.Domain.PhoneNumber` already applies for the identical
/// shape check in a separate product. Reachability (does this number ring) still is not decided here, for
/// the identical reason that type gives: only a live gateway call could ever know, and pretending
/// otherwise would put a network call behind a constructor.</para>
///
/// <para>A second, independent implementation of `Ago.Calendar.Domain.PhoneNumber`'s own logic, not a
/// shared reference - `ago-chat` and `ago-calendar` are separate repositories with no shared package for
/// domain types (`docs/architecture/repositories.md`), and this rule is small and stable enough that
/// duplicating it once is cheaper and more honest than inventing a cross-product dependency for eight
/// lines of digit-counting.</para>
/// </summary>
public readonly record struct PhoneNumber
{
    private const int MinDigits = 8;
    private const int MaxDigits = 15;

    public PhoneNumber(string value)
    {
        Value = Normalise(value);
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static string Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var digits = new StringBuilder(MaxDigits);
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
                continue;
            }

            if (!IsIgnorableSeparator(character))
            {
                throw new ArgumentException(
                    $"'{value}' is not a phone number: unexpected character '{character}'.", nameof(value));
            }
        }

        if (digits.Length is < MinDigits or > MaxDigits)
        {
            throw new ArgumentException(
                $"A phone number must carry {MinDigits}-{MaxDigits} digits; '{value}' carries {digits.Length}.",
                nameof(value));
        }

        if (digits[0] == '0')
        {
            throw new ArgumentException(
                $"A phone number must be in international form (country code first); '{value}' starts with 0.",
                nameof(value));
        }

        return string.Concat("+", digits);
    }

    private static bool IsIgnorableSeparator(char character) =>
        character is ' ' or '-' or '.' or '+' or '(' or ')';
}
