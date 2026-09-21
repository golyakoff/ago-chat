namespace Ago.Chat.Infrastructure.RuStore;

/// <summary>
/// `26-04`/`adr/0180` §1-2: bound from `Push:RuStore:*`, read by <c>Ago.Chat.Worker</c> only - never
/// <c>Ago.Chat.Api</c>, never <c>Ago.Chat.Webhooks</c>. That is why this options type, its
/// <c>.AddOptions&lt;RuStoreOptions&gt;().ValidateOnStart()</c> call and the
/// <c>AddHttpClient&lt;RuStorePushSender&gt;</c> registration all live directly in
/// <c>Ago.Chat.Worker/Program.cs</c> rather than in <c>Ago.Chat.Module.ChatModule.ConfigureServices</c>,
/// which every one of the three serving hosts calls: binding a required
/// <see cref="ServiceToken"/> there with <c>.ValidateOnStart()</c> would fail
/// <c>Ago.Chat.Api</c>'s and <c>Ago.Chat.Webhooks</c>' own startup the moment their environment does not
/// carry a credential `push-notifications.md`'s own secrets table says they must never hold - the same
/// "the credential reaches exactly one deployable" property that table states as a design choice, not
/// an accident of where the code happens to live.
/// </summary>
public sealed class RuStoreOptions
{
    public const string SectionName = "Push:RuStore";

    /// <summary>RuStore's own documented send-API host - a public, well-known URL, not a secret, hence
    /// the real default (the same "hardcode the provider's real base URL, let options override it for a
    /// test's own fake host" shape <c>YooKassaOptions.BaseUrl</c> already establishes).</summary>
    public string BaseUrl { get; set; } = "https://vkpns.rustore.ru/";

    /// <summary>Not a secret - it ships inside the app's own <c>AndroidManifest.xml</c> as
    /// <c>ru.rustore.sdk.pushclient.project_id</c>, readable from any copy of the APK
    /// (`push-notifications.md`'s "The new secret" section). Still deploy-time configuration rather than
    /// a committed value, on the ordinary ground that this repository is public and a deployment
    /// identifier belongs with its neighbours in `.env`, not because it needs `secrets.md`'s own rotation
    /// bookkeeping.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>`RUSTORE_PUSH_SERVICE_TOKEN` in `infra-credentials` - presented directly as
    /// <c>Authorization: Bearer {ServiceToken}</c> on every send, with no OAuth2 mint and therefore no
    /// second host and no token cache (`adr/0180` §1). Rotation class Restart, not the Draining a Google
    /// service account would have allowed for free - RuStore's own documentation does not say whether a
    /// push project may hold two live service tokens at once (`adr/0180` §2), so the weaker class is
    /// recorded rather than guessed.</summary>
    public string ServiceToken { get; set; } = string.Empty;
}
