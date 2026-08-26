using System.Security.Cryptography;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// The one implementation of <see cref="IVisitorSigningKeyRing"/>. Validates the whole key set in its
/// constructor, so a misconfigured rotation is a host that refuses to start rather than a 401 the
/// first visitor discovers.
/// </summary>
public sealed class VisitorSigningKeyRing : IVisitorSigningKeyRing
{
    /// <summary>
    /// The id given to a key that arrived through the legacy single-key setting, which carries no id
    /// of its own. Names the configuration key it came from, because the only reader of a `kid` is a
    /// human working out where a token was signed.
    /// </summary>
    public const string LegacyKeyId = "auth-signing-key";

    /// <summary>
    /// The id given to the random per-process key `Program.cs` falls back to when nothing is
    /// configured at all. Named so that a `kid` of <c>ephemeral</c> in a token is an immediate,
    /// unmistakable "this host is not sharing a key with anything".
    /// </summary>
    public const string EphemeralKeyId = "ephemeral";

    private readonly IClock _clock;
    private readonly TimeSpan _retirementDelay;
    private readonly IReadOnlyList<(SecurityKey Key, DateTimeOffset RetiredAt)> _retired;

    public VisitorSigningKeyRing(VisitorSigningKeyOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        if (options.RetirementDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName}:RetirementDelay must be positive - it is the window during " +
                "which a retired key still validates tokens signed before the rotation.");
        }

        // Not merely "positive": shorter than the token's own lifetime means the drain window closes
        // while honest visitors are still holding tokens signed by the outgoing key, i.e. exactly the
        // mass logout this whole mechanism exists to avoid, only delayed and therefore harder to
        // attribute. Refused rather than clamped: silently widening a number an operator chose is how
        // a configuration surface stops meaning what it says.
        if (options.RetirementDelay < JwtTokenService.VisitorTokenLifetime)
        {
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName}:RetirementDelay is {options.RetirementDelay}, shorter than the " +
                $"visitor token lifetime ({JwtTokenService.VisitorTokenLifetime}). A retired key must stay valid for at " +
                "least as long as a token it signed can still be presented.");
        }

        _retirementDelay = options.RetirementDelay;

        var keys = options.Keys ?? [];
        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName}:Keys is empty - configure at least one key.");
        }

        var duplicateId = keys
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName}:Keys contains {duplicateId.Count()} entries with Id " +
                $"'{duplicateId.Key}'. Ids identify a key in a `kid` header and in a rotation procedure; two keys " +
                "sharing one make both unidentifiable.");
        }

        var active = keys.Where(entry => entry.RetiredAt is null).ToList();
        if (active.Count != 1)
        {
            // The single most important invariant in this file. "Which key issues" must have exactly
            // one answer, at all times, stated by configuration rather than derived from an ordering.
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName}:Keys must contain exactly one key with no RetiredAt - the one " +
                $"that issues. Found {active.Count}. During a rotation the outgoing key gets a RetiredAt and the " +
                "incoming key gets none; at no point are there two of either.");
        }

        Signing = new SigningCredentials(SymmetricKeyFrom(active[0]), SecurityAlgorithms.HmacSha256);
        _retired = keys
            .Where(entry => entry.RetiredAt is not null)
            .Select(entry => ((SecurityKey)SymmetricKeyFrom(entry), entry.RetiredAt!.Value))
            .ToList();
    }

    public SigningCredentials Signing { get; }

    /// <summary>
    /// The active key, plus every retired key whose drain window is still open. A key past its window
    /// is simply absent, so a token it signed fails signature validation like any forgery - which is
    /// the second half of "rotatable": accepting the old key forever would make rotation cost-free
    /// and pointless.
    ///
    /// <para><b>No filtering by `kid`.</b> The header carries one (see
    /// <see cref="VisitorSigningKeyEntry.Id"/>) and this deliberately ignores it, returning the whole
    /// current set for the handler to try. Trying two or three HMAC-SHA256 keys costs a hash each;
    /// requiring the header to name a configured key would reject every token minted before ids
    /// existed, and would turn any disagreement between a token's `kid` and an operator's re-labelling
    /// of the same key value into a mass logout. A `kid` here is a diagnostic, never a decision.</para>
    /// </summary>
    public IReadOnlyList<SecurityKey> ValidationKeys()
    {
        var now = _clock.UtcNow;
        var keys = new List<SecurityKey>(_retired.Count + 1) { Signing.Key };

        foreach (var (key, retiredAt) in _retired)
        {
            if (now < retiredAt + _retirementDelay)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>
    /// Reads whichever of the three configuration forms is present, in this order, and refuses the
    /// one combination that is ambiguous.
    ///
    /// <list type="number">
    /// <item><c>Auth:VisitorSigningKeys:*</c> - the rotatable form this item adds.</item>
    /// <item><c>Auth:SigningKey</c> - the single-key form that was already deployed, mapped to a key
    /// set of exactly one active key. Kept so that shipping this change rotates nothing and logs
    /// nobody out; a deployment moves to the form above at its first rotation, not before.</item>
    /// <item>Neither - a random per-process key, which was already the behaviour and is still correct
    /// for the single-instance `dotnet run` loop `local-dev.md` describes. Wrong for more than one
    /// replica, which is why the manifests set a key (`3-06`).</item>
    /// </list>
    ///
    /// <para><b>Both of the first two set is a startup failure</b>, not a precedence rule. A rotation
    /// edits configuration by hand under time pressure; the failure mode worth designing against is
    /// the half-finished edit that adds the key set and forgets to remove the old setting. Silently
    /// preferring one would make that edit *look* applied while the host signed with the other key.
    /// </para>
    /// </summary>
    public static VisitorSigningKeyRing FromConfiguration(IConfiguration configuration, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(VisitorSigningKeyOptions.SectionName).Get<VisitorSigningKeyOptions>()
                      ?? new VisitorSigningKeyOptions();
        var legacyKey = configuration[VisitorSigningKeyOptions.LegacySingleKeyName];
        var hasLegacyKey = !string.IsNullOrWhiteSpace(legacyKey);

        if (options.Keys.Count > 0 && hasLegacyKey)
        {
            throw new InvalidOperationException(
                $"Both {VisitorSigningKeyOptions.LegacySingleKeyName} and {VisitorSigningKeyOptions.SectionName}:Keys " +
                "are configured, so which key issues visitor tokens has two answers. Remove the former - the rotation " +
                "procedure in ago-root docs/runbooks/secret-rotation.md replaces it with the key set.");
        }

        if (options.Keys.Count == 0)
        {
            options.Keys.Add(hasLegacyKey
                ? new VisitorSigningKeyEntry { Id = LegacyKeyId, Value = legacyKey! }
                : new VisitorSigningKeyEntry
                {
                    Id = EphemeralKeyId,
                    Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                });
        }

        return new VisitorSigningKeyRing(options, clock);
    }

    private static SymmetricSecurityKey SymmetricKeyFrom(VisitorSigningKeyEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw new InvalidOperationException(
                $"Every entry in {VisitorSigningKeyOptions.SectionName}:Keys needs a non-empty Id.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(entry.Value);
        }
        catch (FormatException exception)
        {
            // The message names the id and never the value - an exception message reaches a log, and
            // `17-02` is the item about credentials reaching logs.
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName} key '{entry.Id}' is not valid base64.", exception);
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"{VisitorSigningKeyOptions.SectionName} key '{entry.Id}' decodes to {bytes.Length} bytes; " +
                "HMAC-SHA256 needs at least 32. `openssl rand -base64 32` produces a correct one.");
        }

        return new SymmetricSecurityKey(bytes) { KeyId = entry.Id };
    }
}
