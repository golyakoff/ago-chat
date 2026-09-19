using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SubmitLogoUpload;

/// <summary>`25-160`: the console's own upload call - <see cref="Bytes"/> arrives already read into
/// memory by <c>Ago.Chat.Api</c>'s own endpoint (this item's own Scope: "a synchronous, cheap check
/// only... before anything is decoded"), never a stream this handler reads itself, so the byte-size
/// ceiling can be checked before a single byte is decoded or pushed to storage.</summary>
public sealed record SubmitLogoUpload(SiteId SiteId, OperatorId RequestedBy, string ContentType, byte[] Bytes);

/// <summary>What the upload endpoint returns - the console has nothing else to show until
/// `Ago.Chat.Worker`'s validating consumer finishes (this item's own Scope: "returns immediately; no
/// decode, no dimension check, happens in this request").</summary>
public sealed record LogoUploadAccepted(LogoStatus Status);
