using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.ValueObjects;

public class PersonNameTests
{
    [Fact]
    public void Format_Full_WithMiddleName_ReturnsAllThreeParts()
    {
        PersonName.Format("Иванов", "Иван", "Иванович", PersonNameStyle.Full).Should().Be("Иванов Иван Иванович");
    }

    [Fact]
    public void Format_Full_WithoutMiddleName_SchlopsToLastAndFirst()
    {
        PersonName.Format("Иванов", "Иван", null, PersonNameStyle.Full).Should().Be("Иванов Иван");
    }

    [Fact]
    public void Format_ShortPatronymic_WithMiddleName_AbbreviatesMiddleOnly()
    {
        PersonName.Format("Иванов", "Иван", "Иванович", PersonNameStyle.ShortPatronymic).Should().Be("Иванов Иван И.");
    }

    [Fact]
    public void Format_ShortPatronymic_WithoutMiddleName_SchlopsToLastAndFirst()
    {
        PersonName.Format("Иванов", "Иван", null, PersonNameStyle.ShortPatronymic).Should().Be("Иванов Иван");
    }

    [Fact]
    public void Format_Initials_WithMiddleName_AbbreviatesBoth()
    {
        PersonName.Format("Иванов", "Иван", "Иванович", PersonNameStyle.Initials).Should().Be("Иванов И.И.");
    }

    [Fact]
    public void Format_Initials_WithoutMiddleName_AbbreviatesOnlyFirst()
    {
        PersonName.Format("Иванов", "Иван", null, PersonNameStyle.Initials).Should().Be("Иванов И.");
    }

    [Fact]
    public void Format_Initials_WithEmptyMiddleName_TreatedAsMissing()
    {
        PersonName.Format("Иванов", "Иван", "  ", PersonNameStyle.Initials).Should().Be("Иванов И.");
    }

    [Theory]
    [InlineData("Иванов", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsValidPart_ChecksNonEmpty(string? value, bool expected)
    {
        PersonName.IsValidPart(value).Should().Be(expected);
    }

    [Fact]
    public void IsValidPart_TooLong_IsRejected()
    {
        PersonName.IsValidPart(new string('a', PersonName.MaxPartLength + 1)).Should().BeFalse();
    }

    [Fact]
    public void IsValidOptionalPart_Null_IsValid()
    {
        PersonName.IsValidOptionalPart(null).Should().BeTrue();
    }

    [Fact]
    public void IsValidBirthDate_Today_IsValid()
    {
        PersonName.IsValidBirthDate(DateOnly.FromDateTime(DateTime.UtcNow)).Should().BeTrue();
    }

    [Fact]
    public void IsValidBirthDate_Tomorrow_IsRejected()
    {
        PersonName.IsValidBirthDate(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))).Should().BeFalse();
    }

    [Fact]
    public void IsValidBirthDate_OlderThan120Years_IsRejected()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        PersonName.IsValidBirthDate(today.AddYears(-121)).Should().BeFalse();
    }

    [Fact]
    public void IsValidBirthDate_Exactly120YearsAgo_IsValid()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        PersonName.IsValidBirthDate(today.AddYears(-120)).Should().BeTrue();
    }

    [Fact]
    public void IsCompleteProfile_AllFieldsPresent_IsTrue()
    {
        PersonName.IsCompleteProfile("Иванов", "Иван", new DateOnly(1990, 1, 1), Gender.Male).Should().BeTrue();
    }

    [Fact]
    public void IsCompleteProfile_MiddleNameNotRequired()
    {
        // Отчество не входит в критерий полноты — есть не у всех.
        PersonName.IsCompleteProfile("Иванов", "Иван", new DateOnly(1990, 1, 1), Gender.Female).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "Иван")]
    [InlineData("Иванов", null)]
    public void IsCompleteProfile_MissingLastOrFirstName_IsFalse(string? lastName, string? firstName)
    {
        PersonName.IsCompleteProfile(lastName, firstName, new DateOnly(1990, 1, 1), Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void IsCompleteProfile_MissingBirthDate_IsFalse()
    {
        PersonName.IsCompleteProfile("Иванов", "Иван", null, Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void IsCompleteProfile_MissingGender_IsFalse()
    {
        PersonName.IsCompleteProfile("Иванов", "Иван", new DateOnly(1990, 1, 1), null).Should().BeFalse();
    }

    [Fact]
    public void IsCompleteProfile_InvalidBirthDate_IsFalse()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        PersonName.IsCompleteProfile("Иванов", "Иван", future, Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void FormatOrDefault_ValidNames_FormatsNormally()
    {
        PersonName.FormatOrDefault("Иванов", "Иван", null, PersonNameStyle.Full, "fallback").Should().Be("Иванов Иван");
    }

    [Fact]
    public void FormatOrDefault_MissingLastName_ReturnsFallback()
    {
        PersonName.FormatOrDefault(null, "Иван", null, PersonNameStyle.Full, "Участник").Should().Be("Участник");
    }
}
