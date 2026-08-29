namespace Ago.Chat.Domain.Tests;

/// <summary>`18-04`: a pure factory method with no clock, no database and nothing to fake
/// (testing.md's domain-unit level), the same shape <see cref="CannedResponseTests"/> uses.</summary>
public class ConversationNoteTests
{
    private static readonly ConversationNoteId Id = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId AuthorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_WithAnEmptyBody_Throws(string body) =>
        Assert.Throws<ArgumentException>(() => ConversationNote.Write(Id, ConversationId, AuthorId, body, Now));

    [Fact]
    public void Write_WithAnOversizedBody_Throws() =>
        Assert.Throws<ArgumentException>(
            () => ConversationNote.Write(Id, ConversationId, AuthorId, new string('n', ConversationNote.MaxBodyLength + 1), Now));

    [Fact]
    public void Write_WhenValid_SetsProperties()
    {
        var note = ConversationNote.Write(Id, ConversationId, AuthorId, "Called back, wants a refund.", Now);

        Assert.Equal(Id, note.Id);
        Assert.Equal(ConversationId, note.ConversationId);
        Assert.Equal(AuthorId, note.AuthorId);
        Assert.Equal("Called back, wants a refund.", note.Body);
        Assert.Equal(Now, note.CreatedAt);
    }

    [Fact]
    public void Write_TrimsBody() =>
        Assert.Equal("Called back.", ConversationNote.Write(Id, ConversationId, AuthorId, "  Called back.  ", Now).Body);
}
