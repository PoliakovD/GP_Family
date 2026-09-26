using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Modules.Medical.HealthNotes;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class HealthNoteServiceTests : SqliteTestBase
{
    private readonly HealthNoteService _sut;

    public HealthNoteServiceTests()
    {
        _sut = new HealthNoteService(Db, NullLogger<HealthNoteService>.Instance);
    }

    private static HealthNoteRequest Symptom(string title = "Головная боль", DateTime? at = null, int severity = 7) =>
        new(HealthNoteKind.Symptom, at ?? DateTime.UtcNow.AddMinutes(-5), title, "после работы", false,
            new SymptomData(severity, ["head"], null), null, null, null, null);

    private static HealthNoteRequest Pressure(DateTime at, decimal sys, decimal dia) =>
        new(HealthNoteKind.Metric, at, null, null, false, null, new MetricData("blood_pressure", sys, dia), null, null, null);

    [Fact]
    public async Task Create_StoresNote_AndReturnsTypedPayload()
    {
        var owner = Db.AddUser();

        var (result, item, _) = await _sut.CreateAsync(owner.Id, Symptom());

        result.Should().Be(HealthNoteResult.Success);
        item!.Kind.Should().Be(HealthNoteKind.Symptom);
        item.Symptom!.Severity.Should().Be(7);
        item.Title.Should().Be("Головная боль");
        Db.HealthNotes.Should().ContainSingle(n => n.OwnerUserId == owner.Id);
    }

    [Fact]
    public async Task Create_InvalidPayload_ReturnsInvalid_AndStoresNothing()
    {
        var owner = Db.AddUser();

        var (result, item, error) = await _sut.CreateAsync(owner.Id, Symptom(severity: 42));

        result.Should().Be(HealthNoteResult.Invalid);
        item.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        Db.HealthNotes.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_FarFutureOrAncientTime_Rejected()
    {
        var owner = Db.AddUser();

        (await _sut.CreateAsync(owner.Id, Symptom(at: DateTime.UtcNow.AddDays(30)))).Result.Should().Be(HealthNoteResult.Invalid);
        (await _sut.CreateAsync(owner.Id, Symptom(at: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)))).Result
            .Should().Be(HealthNoteResult.Invalid);
    }

    [Fact]
    public async Task Create_Sleep_UsesWakeTimeAsOccurredAt()
    {
        var owner = Db.AddUser();
        var wake = DateTime.UtcNow.AddHours(-1);
        var req = new HealthNoteRequest(HealthNoteKind.Sleep, DateTime.UtcNow.AddDays(-3), null, null, false,
            null, null, null, null, new SleepData(wake.AddHours(-7), wake, 3));

        var (_, item, _) = await _sut.CreateAsync(owner.Id, req);

        item!.OccurredAt.Should().BeCloseTo(wake, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Create_TitleIsDropped_ForKindsWithoutTitle_AndFlagOnlyKeptForNotes()
    {
        var owner = Db.AddUser();
        var metric = Pressure(DateTime.UtcNow.AddMinutes(-1), 128, 84) with { Title = "лишнее", IncludeInDoctorQuestions = true };

        var (_, item, _) = await _sut.CreateAsync(owner.Id, metric);

        item!.Title.Should().BeNull();
        item.IncludeInDoctorQuestions.Should().BeFalse();

        var note = new HealthNoteRequest(HealthNoteKind.Note, DateTime.UtcNow.AddMinutes(-1), null, "Спросить про железо", true,
            null, null, null, null, null);
        (await _sut.CreateAsync(owner.Id, note)).Item!.IncludeInDoctorQuestions.Should().BeTrue();
    }

    [Fact]
    public async Task Fields_AreCiphertextInDb()
    {
        var owner = Db.AddUser();
        await _sut.CreateAsync(owner.Id, Symptom("Головная боль"));

        var raw = await ReadRawAsync("SELECT Title || '|' || DataJson || '|' || Text FROM HealthNotes");

        raw.Should().NotContain("Головная").And.NotContain("severity").And.NotContain("после работы");
        raw.Should().Contain("enc:v1:");
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnNotes_NewestFirst_AndFiltersByKindAndRange()
    {
        var me = Db.AddUser();
        var other = Db.AddUser();
        var now = DateTime.UtcNow;
        await _sut.CreateAsync(me.Id, Symptom("Старый", now.AddDays(-10)));
        await _sut.CreateAsync(me.Id, Symptom("Свежий", now.AddHours(-1)));
        await _sut.CreateAsync(me.Id, Pressure(now.AddHours(-2), 120, 80));
        await _sut.CreateAsync(other.Id, Symptom("Чужой", now.AddHours(-1)));

        var all = await _sut.ListAsync(me.Id, null, null, null);
        all.Select(n => n.Title).Where(t => t != null).Should().Equal("Свежий", "Старый");
        all.Should().OnlyContain(n => n.Id != Guid.Empty).And.HaveCount(3);

        (await _sut.ListAsync(me.Id, null, null, HealthNoteKind.Metric)).Should().ContainSingle();
        (await _sut.ListAsync(me.Id, now.AddDays(-1), null, null)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_OtherUsersNote_IsNotFound_AndOwnerChangesApply()
    {
        var owner = Db.AddUser();
        var stranger = Db.AddUser();
        var created = (await _sut.CreateAsync(owner.Id, Symptom())).Item!;

        (await _sut.UpdateAsync(stranger.Id, created.Id, Symptom("Взлом"))).Result.Should().Be(HealthNoteResult.NotFound);

        var (result, updated, _) = await _sut.UpdateAsync(owner.Id, created.Id, Symptom("Мигрень", severity: 3));
        result.Should().Be(HealthNoteResult.Success);
        updated!.Title.Should().Be("Мигрень");
        updated.Symptom!.Severity.Should().Be(3);
    }

    [Fact]
    public async Task Delete_OtherUsersNote_IsNotFound_AndKeepsIt()
    {
        var owner = Db.AddUser();
        var stranger = Db.AddUser();
        var created = (await _sut.CreateAsync(owner.Id, Symptom())).Item!;

        (await _sut.DeleteAsync(stranger.Id, created.Id)).Should().Be(HealthNoteResult.NotFound);
        Db.HealthNotes.Should().ContainSingle();

        (await _sut.DeleteAsync(owner.Id, created.Id)).Should().Be(HealthNoteResult.Success);
        Db.HealthNotes.Should().BeEmpty();
    }

    [Fact]
    public async Task MetricSeries_ReturnsAscendingPointsOfRequestedCodeOnly()
    {
        var me = Db.AddUser();
        var now = DateTime.UtcNow;
        await _sut.CreateAsync(me.Id, Pressure(now.AddDays(-2), 130, 85));
        await _sut.CreateAsync(me.Id, Pressure(now.AddDays(-1), 125, 82));
        await _sut.CreateAsync(me.Id, new HealthNoteRequest(HealthNoteKind.Metric, now.AddDays(-1), null, null, false,
            null, new MetricData("pulse", 72, null), null, null, null));

        var series = await _sut.GetMetricSeriesAsync(me.Id, "blood_pressure", null, null);

        series!.Select(p => p.Value).Should().Equal(130m, 125m);
        series![0].Value2.Should().Be(85m);
        (await _sut.GetMetricSeriesAsync(me.Id, "unknown", null, null)).Should().BeNull();
    }

    [Fact]
    public async Task RecentTitles_AreDistinctAndNewestFirst()
    {
        var me = Db.AddUser();
        var now = DateTime.UtcNow;
        await _sut.CreateAsync(me.Id, Symptom("Изжога", now.AddDays(-3)));
        await _sut.CreateAsync(me.Id, Symptom("Головная боль", now.AddDays(-2)));
        await _sut.CreateAsync(me.Id, Symptom("головная боль", now.AddDays(-1)));

        var titles = await _sut.GetRecentTitlesAsync(me.Id, HealthNoteKind.Symptom);

        titles.Should().HaveCount(2);
        titles[0].Should().BeEquivalentTo("головная боль");
        titles[1].Should().Be("Изжога");
    }

    private async Task<string> ReadRawAsync(string sql)
    {
        var conn = Db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
