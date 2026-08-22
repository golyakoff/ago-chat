using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

// Not named `ResolveOperatorIdentity` (matching the folder/namespace) - a type sharing its
// containing namespace's leaf segment name is ambiguous to reference unqualified from within that
// same namespace, found compiling this use case's own test file.
public sealed record ResolveOperatorIdentityQuery(string ExternalSubjectId);

public sealed record OperatorIdentity(OperatorId OperatorId, SiteId SiteId);
