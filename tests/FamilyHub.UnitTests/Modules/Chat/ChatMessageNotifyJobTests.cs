using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Chat.Chat;
using FamilyHub.Modules.Chat.Realtime;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Chat;

public class ChatMessageNotifyJobTests
{
    [Theory]
    [InlineData(Gender.Female, "Иванова Мария написала в чат семьи «Ивановы»")]
    [InlineData(Gender.Male, "Иванова Мария написал в чат семьи «Ивановы»")]
    [InlineData(null, "Иванова Мария написал(а) в чат семьи «Ивановы»")]
    public void BuildBody_NamesAuthorAndFamily_NeverMessageText(Gender? gender, string expected)
    {
        var author = new User { LastName = "Иванова", FirstName = "Мария", MiddleName = "Петровна", Gender = gender };

        ChatMessageNotifyJob.BuildBody(author, "Ивановы").Should().Be(expected);
    }

    [Fact]
    public void BuildBody_WithoutProfile_FallsBackToNeutralName()
    {
        ChatMessageNotifyJob.BuildBody(new User(), "Семья").Should().StartWith("Участник семьи");
    }

    [Fact]
    public void PresenceTracker_TracksViewingPerConnection()
    {
        var tracker = new ChatPresenceTracker();
        var user = Guid.NewGuid();
        var family = Guid.NewGuid();

        tracker.SetViewing("c1", user, family);
        tracker.SetViewing("c2", user, Guid.NewGuid());
        tracker.IsViewing(user, family).Should().BeTrue();

        tracker.Clear("c1");
        tracker.IsViewing(user, family).Should().BeFalse("вторая вкладка смотрит чат другой семьи");
    }

    [Fact]
    public void ParseLinks_AcceptsValidJson_RejectsGarbageAndUnknownKinds()
    {
        var id = Guid.NewGuid();
        ChatService.ParseLinks(null).Should().BeEmpty();
        ChatService.ParseLinks($"[{{\"kind\":0,\"entityId\":\"{id}\"}}]").Should()
            .ContainSingle(l => l.Kind == ChatLinkKind.MedicalRecord && l.EntityId == id);
        ChatService.ParseLinks("not json").Should().BeNull();
        ChatService.ParseLinks($"[{{\"kind\":99,\"entityId\":\"{id}\"}}]").Should().BeNull();
        ChatService.ParseLinks("[{\"kind\":0,\"entityId\":\"00000000-0000-0000-0000-000000000000\"}]").Should().BeNull();
    }
}
