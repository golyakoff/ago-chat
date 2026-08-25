using System.Reflection;

namespace Ago.Chat.Contracts;

/// <summary>
/// `15-06`: <c>GET /healthz/version</c>'s response body - which commit the process answering this
/// request was built from. The gap it closes is a real one: on 2026-08-25 the console served a
/// week-old bundle for a day and nothing anywhere could say so, because every image carried the
/// mutable tag <c>:local</c> and a running pod revealed nothing about its own source.
/// <para>
/// The commit is read from this assembly's own <see cref="AssemblyInformationalVersionAttribute"/>,
/// which the SDK forms as <c>&lt;Version&gt;+&lt;SourceRevisionId&gt;</c> when the build passes
/// <c>-p:SourceRevisionId=&lt;sha&gt;</c> (the Dockerfile does). That placement is deliberate: it is
/// baked into the compiled binary, so it cannot be contradicted by a Deployment env var, a re-pushed
/// tag, or a manifest edit - unlike an image tag, which is a label someone chose and can choose
/// again. The image tag says what was *asked for*; this says what is *running*.
/// </para>
/// </summary>
/// <param name="Host">The host assembly's own name - <c>Ago.Chat.Api</c>, <c>Ago.Chat.Worker</c>,
/// <c>Ago.Chat.Webhooks</c>. Three hosts deploy from one commit, so a mismatch between them is a
/// half-finished deploy, which is precisely what a single-host check cannot see.</param>
/// <param name="Version">The assembly version portion, without the revision suffix.</param>
/// <param name="Commit">The full 40-character commit SHA, or <c>"unknown"</c> when the build did not
/// supply one (a plain <c>dotnet run</c>, or an image built without the build argument). "unknown" is
/// deliberately a value rather than a null or an omitted field - a deployment that cannot name its
/// own commit should say so out loud, not read as an absent feature.</param>
public sealed record BuildInfoResponse(string Host, string Version, string Commit)
{
    /// <summary>The value <see cref="Commit"/> carries when the build supplied no revision.</summary>
    public const string UnknownCommit = "unknown";

    /// <summary>
    /// Reads build metadata off a compiled assembly. Takes the assembly rather than calling
    /// <see cref="Assembly.GetEntryAssembly"/> internally so the parsing is testable without a host
    /// process - under a test runner the entry assembly is the runner, not the thing under test.
    /// </summary>
    public static BuildInfoResponse For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var host = assembly.GetName().Name ?? "unknown";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Parse(host, informational);
    }

    /// <summary>
    /// Splits an informational version into its version and revision halves. Internal seam for
    /// <see cref="For"/>, exposed so the string handling can be tested directly against the shapes
    /// the SDK actually emits.
    /// </summary>
    public static BuildInfoResponse Parse(string host, string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new BuildInfoResponse(host, UnknownCommit, UnknownCommit);
        }

        // The SDK appends "+<SourceRevisionId>". A SemVer pre-release part uses '-' and stays with
        // the version; only '+' introduces build metadata, so the first '+' is the whole split.
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new BuildInfoResponse(host, informationalVersion, UnknownCommit);
        }

        var version = informationalVersion[..plus];
        var revision = informationalVersion[(plus + 1)..];

        return new BuildInfoResponse(
            host,
            version.Length == 0 ? UnknownCommit : version,
            revision.Length == 0 ? UnknownCommit : revision);
    }
}
