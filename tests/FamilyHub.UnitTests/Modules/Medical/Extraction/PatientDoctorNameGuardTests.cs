using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Живой баг (план "качество ИИ-распознавания анализов", Этап 2): на бланке лаборатории
/// единственное ФИО целиком принадлежит пациенту ("Тестовна Теста Тестовична | Ж | 22.02.2002"),
/// а модель нередко подставляет его в поле "doctor" — направившего врача на бланке часто просто
/// нет. PatientDoctorNameGuard сравнивает извлечённое "doctor" с реальным ФИО пациента записи во
/// всех трёх стилях написания (см. PersonName.Format).
/// </summary>
public class PatientDoctorNameGuardTests
{
    [Theory]
    [InlineData("Тестовна Теста Тестовична")] // ФИО целиком (PersonNameStyle.Full)
    [InlineData("Тестовна Теста Т.")] // сокращённое отчество (PersonNameStyle.ShortPatronymic)
    [InlineData("Тестовна Т.Т.")] // одни инициалы (PersonNameStyle.Initials)
    [InlineData("ТЕСТОВНА ТЕСТА ТЕСТОВИЧНА")] // КАПС — типично для бланков
    [InlineData("Тестовна Т. Т.")] // инициалы с пробелом между точкой и буквой
    [InlineData("Тестовна Т.Т., терапевт")] // с хвостом специальности
    public void IsPatientName_ExtractedDoctorMatchesPatientInAnyStyle_ReturnsTrue(string extractedDoctor)
    {
        var result = PatientDoctorNameGuard.IsPatientName(extractedDoctor, "Теста", "Тестовна", "Тестовична");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsPatientName_DifferentPersonWithSameSurname_ReturnsFalse()
    {
        // Оба инициала пациента ("Теста Тестовична" → "Т.Т.") совпадают буква в букву — важно не
        // потерять кратность при сравнении (см. класс-докстринг): другой врач с той же фамилией и
        // ДРУГИМ первым инициалом не должен пройти проверку.
        var result = PatientDoctorNameGuard.IsPatientName("Тестовна Б.Т.", "Теста", "Тестовна", "Тестовична");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPatientName_UnrelatedDoctorName_ReturnsFalse()
    {
        var result = PatientDoctorNameGuard.IsPatientName("Иванов И.И.", "Теста", "Тестовна", "Тестовична");

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPatientName_NoExtractedDoctor_ReturnsFalse(string? extractedDoctor)
    {
        var result = PatientDoctorNameGuard.IsPatientName(extractedDoctor, "Теста", "Тестовна", "Тестовична");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPatientName_PatientNameUnknown_ReturnsFalse()
    {
        // Ни фамилии, ни имени пациента не известно (профиль не заполнен) — сравнивать не с чем,
        // не должны случайно отбросить настоящего врача из-за пустых кандидатов.
        var result = PatientDoctorNameGuard.IsPatientName("Иванов И.И.", null, null, null);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPatientName_PatientWithoutMiddleName_StillMatchesTwoPartInitials()
    {
        var result = PatientDoctorNameGuard.IsPatientName("Смит Д.", "Джон", "Смит", null);

        result.Should().BeTrue();
    }
}
