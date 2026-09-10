namespace Ago.Chat.Domain;

/// <summary>
/// `25-43`: "this priceable thing" - the opaque string a developer chooses the moment they build
/// whatever feature it prices (a seat, the marginal seat rate, a future add-on), the same "a plain
/// string wrapper this assembly stores and compares, never a type anything is enumerated by" shape
/// <see cref="ModuleKey"/> already establishes for an unrelated opaque identifier. Identical charset
/// to <see cref="ModuleKey"/> (lowercase ASCII letters, digits, `_`/`-`) for the identical reason: this
/// value crosses into a route segment and a database column, never free text a human types.
///
/// <para><b>Unlike <see cref="ModuleKey"/>, a price key's own closed set is real and checked - by
/// <see cref="PricedResourceKeys"/>, not by this type.</b> This constructor validates only shape (is
/// this a legal-looking key at all), the same narrow job <see cref="ModuleKey"/>'s own constructor
/// does. Whether a given key is one code has actually registered a price for - the platform owner's
/// own boundary, `25-43`'s own first decision ("code registers the resource's own key... the owner
/// only ever sets or changes the Rouble figure for a key that already exists") - is
/// <see cref="PricedResourceKeys.IsKnown"/>'s job, checked once, at the one place a new key could ever
/// be typed in: <c>PublishPriceVersionHandler</c>. Keeping that check out of this constructor is
/// deliberate: every other <see cref="PriceKey"/> in this codebase (read paths, the already-registered
/// constants on <see cref="SubscriptionTierBands"/>) constructs one from a value already known to be
/// legitimate, and forcing every one of those call sites to handle "not a known key" would spread a
/// check that belongs at exactly one boundary into every reader.</para>
/// </summary>
public readonly record struct PriceKey
{
    public const int MaxLength = 64;

    public PriceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A price key cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"A price key cannot exceed {MaxLength} characters.", nameof(value));
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)
                && character is not ('_' or '-'))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid price key: only lowercase ASCII letters, digits, '_' and '-' "
                    + "are allowed.",
                    nameof(value));
            }
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
