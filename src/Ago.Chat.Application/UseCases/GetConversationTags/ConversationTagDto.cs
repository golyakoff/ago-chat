namespace Ago.Chat.Application.UseCases.GetConversationTags;

/// <summary>
/// `19-02`: this endpoint's own response shape, distinct from `CreateTag`'s <c>TagDto</c> - the one
/// place the console needs to render "AI tagged this" versus "an operator tagged this"
/// (`adr/0078`'s kind 2 Done-when). <see cref="Source"/> is the CLR member name of
/// <see cref="Domain.TagSource"/>, not the enum itself - the same "read model returns a plain
/// projection, not a domain type" shape <see cref="Abstractions.ConversationSummaryItem.State"/>
/// already establishes for the identical reason: a wire DTO in this codebase never carries a domain
/// enum directly.
///
/// <para>Every other tag-vocabulary DTO (<c>ListTags</c>, <c>CreateTag</c>, <c>RenameTag</c>) keeps
/// using the plain <c>TagDto</c> - a site's tag *vocabulary* has no per-conversation source to carry,
/// only an applied *association* does, so only this one read (the one join query
/// <see cref="Abstractions.ITagRepository.GetForConversationAsync"/> reaches) gains the field.</para>
/// </summary>
public sealed record ConversationTagDto(Guid Id, string Name, DateTimeOffset CreatedAt, string Source);
