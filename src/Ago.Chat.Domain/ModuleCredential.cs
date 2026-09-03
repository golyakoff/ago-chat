namespace Ago.Chat.Domain;

/// <summary>
/// `22-02`: the secret half of an <see cref="EnabledModule"/> row - "site X has module K enabled,
/// answered at this entry point, proven by this credential." Stored beside
/// <see cref="EnabledModule.EntryPoint"/> for the identical reason: both are coordinates a site owner
/// configures once on this side and once again on the module deployment's own side (a matched
/// deployment setting, the same manual-coordination shape `ChatModuleTaskOptions.TenantPublicKey`
/// already uses in `ago-calendar` today) - there is no live provisioning handshake between the two
/// products, and inventing one was out of this item's scope (see this repository's own report for the
/// argument).
///
/// <para><b>Opaque to Chat, exactly like <see cref="ModuleKey"/>.</b> This type validates shape only
/// (non-empty, bounded length) - never meaning. Chat never inspects a credential's bytes beyond using
/// them as an HMAC key when <c>Ago.Chat.Infrastructure.Modules</c> signs a per-call token; it does not
/// know or care whether the module on the other end is Calendar, FAQ, or anything invented after
/// this item, which is the whole point of `adr/0065` decision 2.</para>
///
/// <para><b><see cref="MinLength"/> is a floor against an operator typing something trivial
/// ("secret", "1234"), not a real entropy check</b> - this type cannot tell a random 32-byte key from
/// 32 repeated characters, and does not pretend to. Stated here rather than left implicit, per this
/// project's own instruction to say plainly where a value object's validation stops.</para>
/// </summary>
public readonly record struct ModuleCredential
{
    public const int MinLength = 16;

    public const int MaxLength = 256;

    public ModuleCredential(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A module credential cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length < MinLength)
        {
            throw new ArgumentException(
                $"A module credential must be at least {MinLength} characters long.", nameof(value));
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A module credential cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = trimmed;
    }

    public string Value { get; }

    // Deliberately not overridden: the default record-struct ToString() would print the secret into
    // any log statement, exception message, or test failure output that happens to interpolate this
    // type - the same reason a password or API key type in a well-reviewed codebase never gets a
    // convenience ToString(). Callers that need the raw value ask for .Value explicitly.
    public override string ToString() => "ModuleCredential(***)";
}
