namespace Ago.Chat.FakeCrm;

public enum FakeCrmBehaviorKind
{
    Succeeds,
    ServerError,
    Hang,
}

/// <summary>Thrown by <see cref="FakeCrmBehavior.Parse"/> for an <c>X-Fake-Crm-Behavior</c> value this
/// harness does not recognise - Program.cs turns this into a <c>400</c>, distinct from the <c>401</c>
/// a signature failure produces, so a driver pointing this harness at a typo'd behaviour string gets a
/// different, more obvious failure than "the signature must be wrong."</summary>
public sealed class FakeCrmBehaviorException(string message) : Exception(message);

/// <summary>
/// The three personalities selectable via the <c>X-Fake-Crm-Behavior</c> header on
/// <c>POST /webhooks/deliver</c>: <c>succeeds</c> (also the default when the header is absent),
/// <c>500</c>/<c>503</c>/<c>5xx</c>, and <c>hang</c>/<c>hang-&lt;seconds&gt;</c>. The fourth personality,
/// "disappears," is deliberately not parsed here at all - see <see cref="DisappearedConnectionListener"/>
/// and this project's README for why a TCP-level refusal cannot be chosen from inside an HTTP request
/// it never gets to parse.
/// </summary>
public readonly record struct FakeCrmBehavior(FakeCrmBehaviorKind Kind, int StatusCode, TimeSpan? HangDuration)
{
    public static readonly FakeCrmBehavior Succeeds = new(FakeCrmBehaviorKind.Succeeds, StatusCodes.Status200OK, null);

    // HangDuration null here specifically means "no configured duration" - Program.cs reads that as
    // Timeout.InfiniteTimeSpan, i.e. only the caller's own timeout or cancellation ever ends this one,
    // never this harness responding first.
    private static readonly FakeCrmBehavior IndefiniteHang = new(FakeCrmBehaviorKind.Hang, StatusCodes.Status200OK, null);

    public static FakeCrmBehavior Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Succeeds;
        }

        var value = header.Trim();
        return value switch
        {
            "succeeds" => Succeeds,
            "500" => new FakeCrmBehavior(FakeCrmBehaviorKind.ServerError, StatusCodes.Status500InternalServerError, null),
            "503" or "5xx" => new FakeCrmBehavior(FakeCrmBehaviorKind.ServerError, StatusCodes.Status503ServiceUnavailable, null),
            "hang" => IndefiniteHang,
            _ when value.StartsWith("hang-", StringComparison.OrdinalIgnoreCase) => ParseTimedHang(value),
            _ => throw new FakeCrmBehaviorException(
                $"Unknown X-Fake-Crm-Behavior '{value}' - expected succeeds|500|503|5xx|hang|hang-<seconds> (e.g. hang-30s)."),
        };
    }

    private static FakeCrmBehavior ParseTimedHang(string value)
    {
        var secondsText = value["hang-".Length..].TrimEnd('s', 'S');
        if (!int.TryParse(secondsText, out var seconds) || seconds < 0)
        {
            throw new FakeCrmBehaviorException($"Unknown X-Fake-Crm-Behavior '{value}' - expected hang-<seconds>, e.g. hang-30s.");
        }

        return new FakeCrmBehavior(FakeCrmBehaviorKind.Hang, StatusCodes.Status200OK, TimeSpan.FromSeconds(seconds));
    }
}
