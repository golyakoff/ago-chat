namespace Ago.Chat.Domain;

/// <summary>
/// `26-03`/`adr/0179` §1: one row per (operator, app installation) - never per token, which is the
/// single decision that makes FCM token rotation work at all. A small aggregate root of exactly one
/// entity, in the shape <see cref="WebhookEndpoint"/> already has.
///
/// <para><b>Why this is a Domain type and not a bare row in Infrastructure.</b> It owns two real rules
/// that must not live in a handler: <see cref="Token"/> has a bounded length and may not be blank (the
/// same bounded-value discipline <see cref="MessageBody"/> and
/// <see cref="ChannelDelivery.MaxProviderDetailLength"/> apply), and a revoked device sends nothing
/// until a fresh registration revives it. The alternative - a nullable column a query happens to
/// filter on - puts that second rule in every caller's `WHERE` clause, and it drifts the first time
/// somebody writes a second query. It is deliberately *not* a type with children or a state machine:
/// there are exactly two mutations, <see cref="Refresh"/> and <see cref="Revoke"/>, and `adr/0179`
/// calls both of them idempotent - stated once here rather than at each call site.</para>
///
/// <para><b>`26-122`: identity moved from `(OperatorId, InstallationId)` to `(OperatorId, DeviceId)`.</b>
/// `26-83` found one operator holding five live rows (one FCM + four stale RuStore) from repeated
/// reinstalls/re-logins - `InstallationId` is generated fresh by `DataStoreInstallationId` on every
/// install (its own doc comment: "a stable, per-install identifier", which is exactly the problem: an
/// install is not a device, and a reinstall makes a new one), so keying on it could never dedup across
/// a reinstall. <see cref="DeviceId"/> is the client's stable-per-physical-device value (Android's
/// `Settings.Secure.ANDROID_ID`, chosen over a second stored UUID precisely because it survives what
/// <see cref="InstallationId"/> does not) - checked by `OperatorDeviceConfiguration`'s own
/// `ux_operator_devices_operator_device` unique index, the identical "the index is the backstop, the
/// write path is the real mechanism" split the old installation index already drew, moved onto the new
/// key. <see cref="DeviceId"/> is nullable only for rows written before this column existed; every
/// registration from an updated client always carries one, and `RegisterOperatorDeviceHandler`
/// adopts (backfills) an old installation-keyed row the first time it sees one.</para>
///
/// <para><b><see cref="InstallationId"/> is retained, not replaced</b> - `DELETE
/// /api/v1/me/devices/{installationId}` (sign-out) still addresses a row by it, so <see cref="Refresh"/>
/// keeps it current: a reinstall's fresh install id must overwrite the old one on this row, or a later
/// sign-out from the *new* install would address an id nothing here recognises and silently do nothing.
/// It is otherwise diagnostic - "which install last held this device" - the same role `Platform`
/// already plays.</para>
///
/// <para><b>A device belongs to an <see cref="Operator"/>, not to a bare Keycloak identity</b> -
/// `operators` is itself uniquely indexed on `(external_subject_id, site_id)`, and one person may hold
/// several tenancies. The fan-out events this table exists to serve
/// (`Ago.Chat.Contracts.ConversationAssignedToOperator`) already name an <see cref="Domain.OperatorId"/>,
/// so keying here on anything else would add a lookup to reach the same answer, and would risk a
/// notification about one tenant reaching a device registered while the operator worked for another
/// (`tenant-isolation.md`).</para>
/// </summary>
public sealed class OperatorDevice
{
    /// <summary>Generous rather than tight - no provider's registration token format is pinned by any
    /// spec this system depends on, and a bound exists only so one row can never become an unbounded
    /// write, the identical reasoning <see cref="MessageBody.MaxLength"/> states for itself.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>The client-generated identifier is expected to be a UUID or similarly short opaque
    /// string; bounded for the same reason <see cref="MaxTokenLength"/> is, not because anything
    /// observed needs more.</summary>
    public const int MaxInstallationIdLength = 256;

    /// <summary>`26-122`: bounded the identical way <see cref="MaxInstallationIdLength"/> is - Android's
    /// `Settings.Secure.ANDROID_ID` is a short hex string, and the stored-UUID fallback is the same
    /// shape <see cref="InstallationId"/> already uses, so one bound serves both.</summary>
    public const int MaxDeviceIdLength = 256;

    public const int MaxPlatformLength = 32;

    /// <summary>Bounded for the identical reason <see cref="ChannelDelivery.MaxProviderDetailLength"/>
    /// is - <see cref="FailureReason"/>'s own doc comment already states this rule; the constant lives
    /// here instead of a magic number in <see cref="RecordSendFailure"/> for the same "the bound is
    /// part of the invariant, not the call site" reason <see cref="MaxTokenLength"/> already is.</summary>
    public const int MaxFailureReasonLength = 2000;

    public OperatorDeviceId Id { get; }

    public SiteId SiteId { get; }

    public OperatorId OperatorId { get; }

    /// <summary>Client-generated, stable for the life of one app install. Before `26-122` this, with
    /// <see cref="OperatorId"/>, was the row's identity; a reinstall regenerates it
    /// (`DataStoreInstallationId`'s own doc comment), which is exactly why it could not be. Kept current
    /// by <see cref="Refresh"/> so `DELETE /api/v1/me/devices/{installationId}` (sign-out) can still
    /// address this row from whichever install currently holds it - see this type's own remarks.</summary>
    public string InstallationId { get; private set; } = string.Empty;

    /// <summary>`26-122`: the row's real identity, with <see cref="OperatorId"/> - a value stable across
    /// a reinstall, unlike <see cref="InstallationId"/>. `null` only for a row written before this column
    /// existed; see this type's own remarks for how such a row is adopted.</summary>
    public string? DeviceId { get; private set; }

    public PushProvider Provider { get; private set; }

    /// <summary>`'android'` today - diagnostic only, the same "not part of any lookup key" role
    /// <see cref="ExternalChannelAddress"/>'s own channel-neutral remarks give for data that describes
    /// a row rather than identifies it.</summary>
    public string Platform { get; private set; } = string.Empty;

    /// <summary>The provider's own registration token - a value <em>on</em> this row, replaced in place
    /// by <see cref="Refresh"/> on every rotation, never a second row. This is the whole of the
    /// token-rotation story (`adr/0179` §1): a table keyed on the token instead would accumulate one
    /// dead row per rotation forever.</summary>
    public string Token { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Rewritten on every <see cref="Refresh"/> - what a future operational query sorts by to
    /// find a dead install, once `26-04`/`26-05` exist to run one.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary><see langword="null"/> for a live device. Set by sign-out, by the provider reporting the
    /// token gone, by `OperatorRemovedConsumer`'s own added call, or (`26-123`/`adr/0185`) by
    /// `OperatorDevicePruneJob` once <see cref="LastSeenAt"/> has not moved for
    /// `OperatorDevicePruneJobOptions.Threshold` - `adr/0179`'s original "never by a timer" ruling held
    /// only while the provider reliably reported a dead token, which `26-83`/`26-122` found is not true
    /// for every RuStore token a reinstall leaves behind; see `adr/0185` for the full reasoning and why
    /// the default threshold (14 days) is well clear of a live device's own real refresh cadence.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Not written by this item - `26-04`/`26-05`'s own transient-failure path
    /// (`IPushSender`/`PushSendOutcome.TransientFailure`) is the only caller this column is shaped for.
    /// Present now so that work needs no second migration.</summary>
    public DateTimeOffset? LastFailureAt { get; private set; }

    /// <summary>The provider's own short error code, bounded the identical way
    /// <see cref="ChannelDelivery.MaxProviderDetailLength"/> is and for the identical reason: a failure
    /// reason is a code or a phrase, never an essay. Not written by this item - see
    /// <see cref="LastFailureAt"/>'s own remarks.</summary>
    public string? FailureReason { get; private set; }

    private OperatorDevice(
        OperatorDeviceId id, SiteId siteId, OperatorId operatorId, string installationId, string? deviceId,
        PushProvider provider, string platform, string token, DateTimeOffset createdAt, DateTimeOffset lastSeenAt,
        DateTimeOffset? revokedAt)
    {
        Id = id;
        SiteId = siteId;
        OperatorId = operatorId;
        InstallationId = installationId;
        DeviceId = deviceId;
        Provider = provider;
        Platform = platform;
        Token = token;
        CreatedAt = createdAt;
        LastSeenAt = lastSeenAt;
        RevokedAt = revokedAt;
    }

    // EF Core materialization only (WebhookEndpoint's own precedent) - never called by domain code.
    private OperatorDevice()
    {
    }

    /// <summary>
    /// The first registration of one app install for one tenancy. `RegisterOperatorDeviceHandler`
    /// calls this only when no row for `(operatorId, deviceId)` (falling back to `(operatorId,
    /// installationId)` for a pre-`26-122` row) already exists - every later call for the same device is
    /// <see cref="Refresh"/>, which is what makes the whole route an idempotent upsert (`adr/0179` §1)
    /// rather than a second insert.
    ///
    /// <para><paramref name="deviceId"/> defaults to <see langword="null"/> only so the many existing
    /// callers that predate `26-122` and do not care about device identity keep compiling; every real
    /// registration from `RegisterOperatorDeviceHandler` always passes one.</para>
    /// </summary>
    public static OperatorDevice Register(
        OperatorDeviceId id, SiteId siteId, OperatorId operatorId, string installationId, PushProvider provider,
        string platform, string token, DateTimeOffset now, string? deviceId = null)
    {
        ValidateInstallationId(installationId);
        ValidateDeviceId(deviceId);
        ValidatePlatform(platform);
        ValidateToken(token);
        return new(id, siteId, operatorId, installationId, deviceId, provider, platform, token, now, lastSeenAt: now, revokedAt: null);
    }

    /// <summary>
    /// Writes a (possibly unchanged) token, touches <see cref="LastSeenAt"/>, and clears
    /// <see cref="RevokedAt"/>/<see cref="LastFailureAt"/>/<see cref="FailureReason"/> - the whole of
    /// what `PUT /api/v1/me/devices/{installationId}` does to an existing row (`adr/0179` §1). Clearing
    /// <see cref="RevokedAt"/> is deliberate, not an oversight: a device that signed out and later signs
    /// back in on the same install is a fresh registration reviving a stale row, not a new one, and
    /// this type's own remarks call that its second real rule ("a revoked device sends nothing until a
    /// fresh registration revives it").
    ///
    /// <para>`26-122`: <paramref name="installationId"/> and <paramref name="deviceId"/> default to
    /// <see langword="null"/> ("leave unchanged") for the identical "existing callers keep compiling"
    /// reason <see cref="Register"/>'s own default does. `RegisterOperatorDeviceHandler` always passes
    /// both - <paramref name="installationId"/> because a reinstall found via <see cref="DeviceId"/>
    /// carries a fresh one (this type's own remarks on why <see cref="InstallationId"/> must track it),
    /// and <paramref name="deviceId"/> because a row found only through the pre-`26-122` installation
    /// fallback is adopted here, backfilling a <see langword="null"/> <see cref="DeviceId"/> exactly
    /// once.</para>
    /// </summary>
    public void Refresh(
        PushProvider provider, string platform, string token, DateTimeOffset now,
        string? installationId = null, string? deviceId = null)
    {
        ValidatePlatform(platform);
        ValidateToken(token);
        if (installationId is not null)
        {
            ValidateInstallationId(installationId);
        }

        if (deviceId is not null)
        {
            ValidateDeviceId(deviceId);
        }

        Provider = provider;
        Platform = platform;
        Token = token;
        LastSeenAt = now;
        RevokedAt = null;
        LastFailureAt = null;
        FailureReason = null;
        InstallationId = installationId ?? InstallationId;
        DeviceId = deviceId ?? DeviceId;
    }

    /// <summary>
    /// Idempotent, deliberately unlike <see cref="WebhookEndpoint.Revoke"/> - `adr/0179` states plainly
    /// that both of this type's mutations are idempotent, and a sign-out DELETE, a provider-reported
    /// `UNREGISTERED`, and an operator-removal sweep must each be safe to call twice (at-least-once
    /// delivery, or a double-clicked sign-out button) without surfacing a "you already did that" error
    /// for a caller with no way to have known.
    /// </summary>
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
    }

    /// <summary>
    /// `26-05`: the one caller <see cref="LastFailureAt"/>/<see cref="FailureReason"/>'s own doc
    /// comments were written for - <c>NotifyOperatorDevicesHandler</c>'s own
    /// <c>PushSendOutcome.TransientFailure</c> path (RuStore's own credential-fault outcomes, `401`/
    /// `403`/`429`/`500` - never a statement about this device). Deliberately <em>not</em> a
    /// revocation: the device's own registration is still believed live, so <see cref="RevokedAt"/> is
    /// untouched and a future send is retried exactly as before - only a <c>TokenGone</c>-shaped
    /// outcome ever calls <see cref="Revoke"/>. Not cleared by a later successful send, on the same
    /// "this is a historical fact about the row, not a currently-failing flag" reading
    /// <see cref="LastSeenAt"/>'s own remarks give the sibling field that <em>is</em> cleared, by
    /// <see cref="Refresh"/> alone.
    /// </summary>
    public void RecordSendFailure(string reason, DateTimeOffset now)
    {
        LastFailureAt = now;
        FailureReason = reason.Length > MaxFailureReasonLength ? reason[..MaxFailureReasonLength] : reason;
    }

    private static void ValidateInstallationId(string installationId)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new ArgumentException("Installation id cannot be empty.", nameof(installationId));
        }

        if (installationId.Length > MaxInstallationIdLength)
        {
            throw new ArgumentException(
                $"Installation id cannot exceed {MaxInstallationIdLength} characters.", nameof(installationId));
        }
    }

    /// <summary>`26-122`: unlike <see cref="ValidateInstallationId"/>, <see langword="null"/> is a valid
    /// input here (a pre-migration row, or "leave unchanged" on <see cref="Refresh"/>) - only a
    /// non-null-but-blank or overlong value is a caller mistake.</summary>
    private static void ValidateDeviceId(string? deviceId)
    {
        if (deviceId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id cannot be blank.", nameof(deviceId));
        }

        if (deviceId.Length > MaxDeviceIdLength)
        {
            throw new ArgumentException($"Device id cannot exceed {MaxDeviceIdLength} characters.", nameof(deviceId));
        }
    }

    private static void ValidatePlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new ArgumentException("Platform cannot be empty.", nameof(platform));
        }

        if (platform.Length > MaxPlatformLength)
        {
            throw new ArgumentException($"Platform cannot exceed {MaxPlatformLength} characters.", nameof(platform));
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token cannot be empty.", nameof(token));
        }

        if (token.Length > MaxTokenLength)
        {
            throw new ArgumentException($"Token cannot exceed {MaxTokenLength} characters.", nameof(token));
        }
    }
}
