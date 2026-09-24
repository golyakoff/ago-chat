namespace Ago.Chat.Infrastructure.Fcm;

/// <summary>
/// `26-100`/`adr/0181`: bound from `Push:Fcm:*`, read by <c>Ago.Chat.Worker</c> only - never
/// <c>Ago.Chat.Api</c> (the internet-facing host, `adr/0181` §4), never <c>Ago.Chat.Webhooks</c>. That is
/// why this options type, its <c>.AddOptions&lt;FcmOptions&gt;().ValidateOnStart()</c> call and the FCM
/// HTTP-client registrations all live directly in <c>Ago.Chat.Worker/Program.cs</c> rather than in the
/// shared <c>ChatModule.ConfigureServices</c>: binding a required <see cref="ServiceAccountJson"/> there
/// would fail the other two hosts' startup the moment their environment does not carry a credential
/// `secrets.md` says they must never hold - the identical isolation
/// <c>Ago.Chat.Infrastructure.RuStore.RuStoreOptions</c>'s own remarks state for the RuStore token.
/// </summary>
public sealed class FcmOptions
{
    public const string SectionName = "Push:Fcm";

    /// <summary>FCM HTTP v1's own documented send-API host - a public, well-known URL, not a secret, hence
    /// the real default (the same "hardcode the provider's real base URL, let options override it for a
    /// test's own fake host" shape <c>RuStoreOptions.BaseUrl</c> and <c>YooKassaOptions.BaseUrl</c> already
    /// establish).</summary>
    public string BaseUrl { get; set; } = "https://fcm.googleapis.com/";

    /// <summary>The Firebase project id - <c>ago-chat-783f7</c>. Not a secret: it is the same public
    /// identifier that ships inside the app's own <c>google-services.json</c>, readable from any copy of
    /// the APK, so it carries a real default (matching <c>RuStoreOptions.ProjectId</c>'s reasoning) rather
    /// than `secrets.md`'s rotation bookkeeping. Still overridable per deployment.</summary>
    public string ProjectId { get; set; } = "ago-chat-783f7";

    /// <summary>`FCM_SERVICE_ACCOUNT_JSON` in `infra-credentials` (bound here via
    /// `Push__Fcm__ServiceAccountJson`) - the whole service-account key file as one JSON string. This is
    /// the one real secret this adapter needs: <c>FcmServiceAccountTokenProvider</c> parses its
    /// `client_email`/`private_key`/`token_uri` and signs the OAuth2 JWT assertion that mints a
    /// short-lived access token. Never committed, never logged, never handed to a host other than the
    /// Worker. Rotation class Draining - a Google service account may hold multiple active keys at once,
    /// so a new key can be added and traffic moved before the old one is deleted, unlike RuStore's
    /// single-token Restart class (`adr/0181`).</summary>
    public string ServiceAccountJson { get; set; } = string.Empty;
}
