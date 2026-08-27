using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

// Not named `ResolveOperatorIdentity` (matching the folder/namespace) - a type sharing its
// containing namespace's leaf segment name is ambiguous to reference unqualified from within that
// same namespace, found compiling this use case's own test file.
//
// `13-07`/`adr/0068`: `RequestedSiteId` is the one new input the whole "one login, several tenants"
// mechanism adds - a client-supplied signal (a header for REST calls, a query-string parameter for
// the SignalR hub handshake - `OperatorIdentityClaimsTransformation`'s own remarks), never trusted to
// *widen* access, only to *select among* rows already proven to belong to this `sub`
// (`ResolveOperatorIdentityHandler`'s own doc comment has the exact algorithm).
public sealed record ResolveOperatorIdentityQuery(string ExternalSubjectId, SiteId? RequestedSiteId = null);

public sealed record OperatorIdentity(OperatorId OperatorId, SiteId SiteId);
