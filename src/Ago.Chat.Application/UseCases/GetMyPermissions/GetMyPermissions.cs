using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetMyPermissions;

/// <summary>`5-08`: no participant/permission check of its own - "what are my own permissions" is
/// always answerable for whoever is asking, the same way asking for your own profile never needs an
/// authorization check beyond "you are authenticated as someone."
///
/// <para>`23-02`: <see cref="Name"/>/<see cref="Email"/> come from the validated token's own `name`/
/// `email` claims, never from anything user-supplied in a request body - the same "identity is a
/// property of the authenticated caller" rule `RegisterSite.ExternalSubjectId`'s own remarks state.
/// This is also the sign-in refresh's own trigger: `GetMyPermissionsHandler` writes these into the
/// caller's `operators` row on every call, which is why this query - "what are my own permissions" -
/// is no longer read-only underneath, even though its own contract stays a query from this command's
/// point of view.</para></summary>
public sealed record GetMyPermissions(OperatorId OperatorId, SiteId SiteId, string? Name = null, string? Email = null);
