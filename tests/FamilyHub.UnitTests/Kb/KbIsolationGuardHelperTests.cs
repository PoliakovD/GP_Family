using FamilyHub.Modules.Medical.Kb;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Kb;

/// <summary>Ветка medicalrecords: KbIsolationGuard.FindViolation вынесен из KbWriter при
/// добавлении второго writer'а (LabAnalyteKbWriter) — тесты на сам общий хелпер, отдельно от
/// структурных guard-тестов в KbIsolationGuardTests.</summary>
public class KbIsolationGuardHelperTests
{
    [Fact]
    public void FindViolation_CleanText_ReturnsNull()
    {
        KbIsolationGuard.FindViolation(["Гемоглобин", "переносит кислород в крови"]).Should().BeNull();
    }

    [Fact]
    public void FindViolation_ContainsGuid_ReturnsViolation()
    {
        KbIsolationGuard.FindViolation(["см. запись 3fa85f64-5717-4562-b3fc-2c963f66afa6"]).Should().NotBeNull();
    }

    [Fact]
    public void FindViolation_ContainsEmail_ReturnsViolation()
    {
        KbIsolationGuard.FindViolation(["контакт: user@example.com"]).Should().NotBeNull();
    }

    [Fact]
    public void FindViolation_ContainsLongDigitSequence_ReturnsViolation()
    {
        KbIsolationGuard.FindViolation(["паспорт 1234567890"]).Should().NotBeNull();
    }

    [Theory]
    [InlineData("UserId 123")]
    [InlineData("FamilyId привязан")]
    [InlineData("Telegram аккаунт клиента")]
    public void FindViolation_ContainsPersonalKeyword_ReturnsViolation(string text)
    {
        KbIsolationGuard.FindViolation([text]).Should().NotBeNull();
    }

    [Fact]
    public void FindViolation_NullAndWhitespaceCandidates_AreSkipped()
    {
        KbIsolationGuard.FindViolation([null, "  ", "гемоглобин"]).Should().BeNull();
    }
}
