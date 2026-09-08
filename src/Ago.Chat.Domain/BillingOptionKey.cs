namespace Ago.Chat.Domain;

/// <summary>
/// `23-86`/`adr/0159`: "this row is a subscription for option K" - the billing catalog's own opaque
/// vocabulary, the identical "opaque string this assembly stores and compares, never a literal it
/// branches on" discipline <see cref="ModuleKey"/>'s own remarks state for the module registry's key.
///
/// <para><b>A distinct type from <see cref="ModuleKey"/>, not a reuse of it, even though the character
/// rules are identical.</b> The two name different vocabularies that happen to share a shape: a
/// <see cref="ModuleKey"/> is Chat's own module registry's key (<c>"calendar"</c>, <c>"faq"</c>) and
/// <see cref="Application.Abstractions.IModuleEntryPointProvider"/>/<see cref="Application.Abstractions.IModulePermissionsProvider"/>
/// resolve it directly; a <see cref="BillingOptionKey"/> is the commercial price list's own SKU
/// (`ago-business`'s own vocabulary, never named here) and
/// <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/> resolves *that* to whichever
/// <see cref="ModuleKey"/> (if any) it turns on. Collapsing the two into one type would make a
/// price-list rename a `ModuleKey` rename too, and would make "the option happens to be spelled the
/// same as the module it grants" a coincidence this codebase would be one refactor away from
/// depending on.</para>
/// </summary>
public readonly record struct BillingOptionKey
{
    public const int MaxLength = 64;

    public BillingOptionKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A billing option key cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"A billing option key cannot exceed {MaxLength} characters.", nameof(value));
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)
                && character is not ('_' or '-'))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid billing option key: only lowercase ASCII letters, digits, '_' and "
                    + "'-' are allowed.",
                    nameof(value));
            }
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
