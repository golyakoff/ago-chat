namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`/`adr/0065` decision 2: "site X has a module with key K enabled" is a row, and this is the
/// key - an opaque string AGO Chat stores and compares, never a type AGO Calendar (or any future
/// module) is referenced by. There is no <c>using Ago.Calendar</c> and no <c>"calendar"</c> literal
/// anywhere in <c>Ago.Chat.*</c>; guard 2 (<c>tests/Ago.Chat.Architecture.Tests</c>) is what makes that
/// a checked property rather than a convention.
///
/// <para><b>Not an enum, for the identical reason <see cref="MessageContentKind"/> is not one.</b> An
/// enum member per module would be the exact moment this assembly learned what a module <em>is</em> -
/// the boundary crossing the whole design exists to prevent, arriving through a data model rather than
/// a <c>ProjectReference</c>. A registry (<c>Application.Abstractions.IEnabledModuleReadStore</c>) is
/// how a caller finds out which keys exist for a site; this type only says what a key may look like.</para>
///
/// <para><b>Narrower charset than <see cref="MessageContentKind"/>: no <c>.</c>.</b> A module key is a
/// single flat name (<c>"calendar"</c>), never a namespaced label - the backlog item's own wording
/// ("lowercase/-/_ , max 64") deliberately does not carry the dot `MessageContentKind` allows for a
/// producer's own hierarchical vocabulary.</para>
/// </summary>
public readonly record struct ModuleKey
{
    public const int MaxLength = 64;

    public ModuleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A module key cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"A module key cannot exceed {MaxLength} characters.", nameof(value));
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)
                && character is not ('_' or '-'))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid module key: only lowercase ASCII letters, digits, '_' and '-' "
                    + "are allowed.",
                    nameof(value));
            }
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
