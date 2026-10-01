using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Enrichment;

/// <summary>Ручные сниппеты кэша поиска, закрепление и набор для суммаризатора (ADR-0018).</summary>
public class SearchCacheSnippetsTests
{
    private static WebSnippet Auto(string url, bool pinned = false) => new($"Заголовок {url}", url, $"текст {url}", Pinned: pinned);

    private static WebSnippet Manual(string url, string kind = SnippetKinds.ManualQuote) =>
        new($"Ручной {url}", url, "цитата", SnippetOrigin.Manual, kind);

    [Fact]
    public void MergeAfterSearch_FreshResultsReplaceAuto_ButManualSurvives()
    {
        var existing = new List<WebSnippet> { Auto("https://old.ru/1"), Manual("https://manual.ru/1") };
        var fresh = new List<WebSnippet> { Auto("https://new.ru/1") };

        var merged = SearchCacheSnippets.MergeAfterSearch(existing, fresh);

        merged.Select(s => s.Url).Should().Equal("https://new.ru/1", "https://manual.ru/1");
        merged.Single(s => s.Url == "https://manual.ru/1").Origin.Should().Be(SnippetOrigin.Manual);
    }

    [Fact]
    public void MergeAfterSearch_PinnedAutoSnippetKeepsPin_AndSurvivesEvenIfFreshSearchDroppedIt()
    {
        var existing = new List<WebSnippet> { Auto("https://pinned.ru/keep", pinned: true), Auto("https://pinned.ru/gone", pinned: true) };
        var fresh = new List<WebSnippet> { Auto("https://pinned.ru/keep"), Auto("https://other.ru") };

        var merged = SearchCacheSnippets.MergeAfterSearch(existing, fresh);

        merged.Single(s => s.Url == "https://pinned.ru/keep").Pinned.Should().BeTrue("закрепление переносится на свежую выдачу");
        merged.Should().Contain(s => s.Url == "https://pinned.ru/gone" && s.Pinned, "закреплённый остаётся, даже если поиск его не вернул");
        merged.Single(s => s.Url == "https://other.ru").Pinned.Should().BeFalse();
    }

    [Fact]
    public void MergeAfterSearch_FreshResultDuplicatingManualUrl_IsNotAddedTwice()
    {
        var merged = SearchCacheSnippets.MergeAfterSearch([Manual("https://same.ru")], [Auto("https://same.ru")]);

        merged.Should().ContainSingle().Which.Origin.Should().Be(SnippetOrigin.Manual);
    }

    [Fact]
    public void MergeAfterReplace_OldEditorCannotWipeManualSnippets()
    {
        var existing = new List<WebSnippet> { Auto("https://a.ru"), Manual("https://manual.ru") };

        var replaced = SearchCacheSnippets.MergeAfterReplace(existing, [Auto("https://b.ru")]);

        replaced.Select(s => s.Url).Should().Equal("https://b.ru", "https://manual.ru");
    }

    [Fact]
    public void CreateManual_ExpertKnowledge_HasNoRealUrl_AndExpertTitle()
    {
        var (snippet, error) = SearchCacheSnippets.CreateManual(SnippetKinds.ExpertKnowledge, null, null, "Дозировка уточняется врачом.", "заметка");

        error.Should().BeNull();
        snippet!.Origin.Should().Be(SnippetOrigin.Manual);
        snippet.Kind.Should().Be(SnippetKinds.ExpertKnowledge);
        SnippetKinds.IsExpertUrl(snippet.Url).Should().BeTrue();
        snippet.Title.Should().Be(SnippetKinds.ExpertSourceLabel);
        snippet.Note.Should().Be("заметка");
    }

    [Fact]
    public void CreateManual_Quote_RequiresHttpUrl_AndRejectsArticleLengthText()
    {
        SearchCacheSnippets.CreateManual(SnippetKinds.ManualQuote, null, null, "текст", null).Error.Should().NotBeNull("цитате нужна ссылка");
        SearchCacheSnippets.CreateManual(SnippetKinds.ManualQuote, "ftp://x.ru", null, "текст", null).Error.Should().NotBeNull();
        SearchCacheSnippets.CreateManual(SnippetKinds.ManualQuote, "not a url", null, "текст", null).Error.Should().NotBeNull();

        var tooLong = new string('а', SnippetKinds.MaxManualTextLength + 1);
        SearchCacheSnippets.CreateManual(SnippetKinds.ManualQuote, "https://x.ru", null, tooLong, null).Error
            .Should().Contain("800", "целые статьи код не принимает");
        SearchCacheSnippets.CreateManual(SnippetKinds.ExpertKnowledge, null, null, tooLong, null).Error.Should().NotBeNull();

        var ok = SearchCacheSnippets.CreateManual(SnippetKinds.ManualQuote, "https://some-untrusted.example/page", null, "цитата", null);
        ok.Error.Should().BeNull("недоверенный домен — не ошибка, а предупреждение в UI");
        ok.Snippet!.Title.Should().Be("some-untrusted.example");
    }

    [Fact]
    public void CreateManual_UnknownKindOrEmptyText_IsRejected()
    {
        SearchCacheSnippets.CreateManual("web", "https://x.ru", null, "текст", null).Error.Should().NotBeNull();
        SearchCacheSnippets.CreateManual(SnippetKinds.ExpertKnowledge, null, null, "   ", null).Error.Should().NotBeNull();
    }

    [Fact]
    public void SnippetJson_RoundTrips_OriginAsString_AndOldRowsReadAsAuto()
    {
        var json = SearchCacheSnippets.Serialize([Manual("https://m.ru")]);
        json.Should().Contain("\"Manual\"", "origin сериализуется строкой — по нему чистка кэша отличает ручные строки");
        SearchCacheSnippets.Parse(json).Single().Origin.Should().Be(SnippetOrigin.Manual);

        // Строки кэша, записанные до ADR-0018, без новых полей.
        var old = SearchCacheSnippets.Parse("[{\"title\":\"t\",\"url\":\"https://o.ru\",\"text\":\"x\"}]");
        old.Single().Origin.Should().Be(SnippetOrigin.Auto);
        old.Single().Pinned.Should().BeFalse();
        SearchCacheSnippets.HasManual(json).Should().BeTrue();
        SearchCacheSnippets.HasManual("[{\"title\":\"t\",\"url\":\"https://o.ru\",\"text\":\"x\"}]").Should().BeFalse();
    }

    // ------------------------------------------------------------------ правка и импорт («взять из готового кэша»)

    [Fact]
    public void ApplyEdit_AutoSnippetBecomesManualQuote_KeepingUrlAndPin()
    {
        var original = Auto("https://vidal.ru/a", pinned: true);

        var (edited, error) = SearchCacheSnippets.ApplyEdit(original, " Новый заголовок ", " исправленный текст ", " почему ");

        error.Should().BeNull();
        edited!.Url.Should().Be("https://vidal.ru/a");
        edited.Title.Should().Be("Новый заголовок");
        edited.Text.Should().Be("исправленный текст");
        edited.Note.Should().Be("почему");
        edited.Pinned.Should().BeTrue();
        edited.Origin.Should().Be(SnippetOrigin.Manual, "правка админа должна пережить автообновление кэша");
        edited.Kind.Should().Be(SnippetKinds.ManualQuote);
    }

    [Fact]
    public void ApplyEdit_ExpertKnowledgeStaysExpert_EmptyTitleFallsBackToLabel()
    {
        var original = Manual("expert://admin/1", SnippetKinds.ExpertKnowledge);

        var (edited, _) = SearchCacheSnippets.ApplyEdit(original, "  ", "новое знание", null);

        edited!.Kind.Should().Be(SnippetKinds.ExpertKnowledge);
        edited.Title.Should().Be(SnippetKinds.ExpertSourceLabel);
    }

    [Fact]
    public void ApplyEdit_RejectsEmptyText_AndTextLongerThanOriginalOrLimit()
    {
        SearchCacheSnippets.ApplyEdit(Auto("https://a.ru"), null, "  ", null).Error.Should().NotBeNull();
        SearchCacheSnippets.ApplyEdit(Auto("https://a.ru"), null, new string('а', SnippetKinds.MaxManualTextLength + 1), null)
            .Error.Should().NotBeNull("короткую авто-выдержку нельзя превратить в статью");

        var longAuto = new WebSnippet("Т", "https://b.ru", new string('б', 1200));
        SearchCacheSnippets.ApplyEdit(longAuto, null, new string('б', 1100), null).Error
            .Should().BeNull("длинную авто-выдержку можно подрезать");
        SearchCacheSnippets.ApplyEdit(longAuto, null, new string('б', 1201), null).Error.Should().NotBeNull();
    }

    [Fact]
    public void Import_AddsOnlySelectedNewUrls_AndNeverOverwritesOwnSnippets()
    {
        var own = new List<WebSnippet> { Manual("https://same.ru/1"), Auto("https://own.ru/1") };
        var source = new List<WebSnippet> { Auto("https://same.ru/1"), Auto("https://src.ru/1"), Auto("https://src.ru/2") };

        var (merged, imported) = SearchCacheSnippets.Import(own, source, ["https://SAME.ru/1", "https://src.ru/2"]);

        imported.Select(s => s.Url).Should().Equal("https://src.ru/2");
        merged.Select(s => s.Url).Should().Equal("https://same.ru/1", "https://own.ru/1", "https://src.ru/2");
        merged[0].Origin.Should().Be(SnippetOrigin.Manual, "свой (ручной) сниппет с тем же URL не перетирается");

        SearchCacheSnippets.Import(own, source, null).Imported.Should().HaveCount(2, "без выбора — все новые URL");
    }
}

public class EnrichmentSnippetFilterManualTests
{
    private static readonly string[] Trusted = ["vidal.ru", "rlsnet.ru"];

    [Fact]
    public void IsEnabled_ManualAndPinnedSnippetsAreEnabledEvenOnUntrustedDomain_ButExplicitOverrideWins()
    {
        var manual = new WebSnippet("m", "https://untrusted.example/x", "t", SnippetOrigin.Manual, SnippetKinds.ManualQuote);
        var pinned = new WebSnippet("p", "https://untrusted.example/y", "t", Pinned: true);
        var plain = new WebSnippet("a", "https://untrusted.example/z", "t");

        EnrichmentSnippetFilter.IsEnabled(manual, Trusted, null).Should().BeTrue();
        EnrichmentSnippetFilter.IsEnabled(pinned, Trusted, null).Should().BeTrue();
        EnrichmentSnippetFilter.IsEnabled(plain, Trusted, null).Should().BeFalse();
        EnrichmentSnippetFilter.IsEnabled(manual, Trusted, new Dictionary<string, bool> { [manual.Url] = false }).Should().BeFalse("явное выключение админом побеждает");
    }

    [Fact]
    public void SelectForSummary_ManualAndPinnedFirst_ThenOrderOrRank_AndMaxNeverCutsThem()
    {
        var snippets = new[]
        {
            new WebSnippet("t0", "https://rlsnet.ru/1", "t"),          // доверенный, ранг 1
            new WebSnippet("t1", "https://vidal.ru/1", "t"),           // доверенный, ранг 0
            new WebSnippet("t2", "https://untrusted.example/2", "t"),  // недоверенный — отфильтруется
            new WebSnippet("m", "https://manual.example/3", "t", SnippetOrigin.Manual, SnippetKinds.ManualQuote),
            new WebSnippet("p", "https://vidal.ru/pinned", "t", Pinned: true),
        };

        var inOrder = EnrichmentSnippetFilter.SelectForSummary(snippets, Trusted, null, 3);
        inOrder.Select(s => s.Title).Should().Equal("m", "p", "t0");

        var byRank = EnrichmentSnippetFilter.SelectForSummary(snippets, Trusted, null, 4, rankOrder: true);
        // Внутри группы «ручные и закреплённые» тоже действует приоритет домена: закреплённый vidal.ru (ранг 0) выше
        // ручного с недоверенного домена; обе группы при этом идут раньше всех остальных.
        byRank.Select(s => s.Title).Should().Equal("p", "m", "t1", "t0");
    }

    [Fact]
    public void SelectForSummary_DisabledOverrideRemovesEvenManual()
    {
        var manual = new WebSnippet("m", "https://manual.example/3", "t", SnippetOrigin.Manual);
        var result = EnrichmentSnippetFilter.SelectForSummary(
            [manual], Trusted, new Dictionary<string, bool> { [manual.Url] = false }, 5);

        result.Should().BeEmpty();
    }
}

public class KbPayloadHashTests
{
    [Fact]
    public void Compute_IgnoresKeyOrderAndWhitespace_ButNotValuesOrArrayOrder()
    {
        var a = KbPayloadHash.Compute("{\"a\":1,\"b\":[1,2],\"c\":{\"x\":\"y\",\"z\":null}}");
        var b = KbPayloadHash.Compute("{ \"c\": { \"z\": null, \"x\": \"y\" }, \"b\": [1, 2], \"a\": 1 }");
        var differentValue = KbPayloadHash.Compute("{\"a\":2,\"b\":[1,2],\"c\":{\"x\":\"y\",\"z\":null}}");
        var differentArrayOrder = KbPayloadHash.Compute("{\"a\":1,\"b\":[2,1],\"c\":{\"x\":\"y\",\"z\":null}}");

        b.Should().Be(a);
        differentValue.Should().NotBe(a);
        differentArrayOrder.Should().NotBe(a);
        a.Should().HaveLength(64);
    }

    [Fact]
    public void Compute_InvalidJson_DoesNotThrow_AndIsDeterministic()
    {
        KbPayloadHash.Compute("не json").Should().Be(KbPayloadHash.Compute("не json"));
    }
}

public class EnrichmentJobStatusSetsTests : SqliteTestBase
{
    [Fact]
    public void Live_IncludesAwaitingStatuses_AndMatchesUniqueIndexFilterOfAllThreeJobTables()
    {
        EnrichmentJobStatusSets.Live.Should().Contain(
            [EnrichmentJobStatus.Pending, EnrichmentJobStatus.Running, EnrichmentJobStatus.Deferred,
             EnrichmentJobStatus.AwaitingSearchApproval, EnrichmentJobStatus.AwaitingResultReview]);
        EnrichmentJobStatusSets.Live.Should().NotContain(
            [EnrichmentJobStatus.Completed, EnrichmentJobStatus.Failed, EnrichmentJobStatus.Skipped]);

        var expected = EnrichmentJobStatusSets.Live.Select(s => (int)s).OrderBy(i => i).ToArray();
        foreach (var type in new[] { typeof(MedicationEnrichmentJob), typeof(VisitMedicationEnrichmentJob), typeof(LabAnalyteEnrichmentJob) })
        {
            var index = Db.Model.FindEntityType(type)!.GetIndexes().Single(i => i.IsUnique);
            var filter = index.GetFilter();
            filter.Should().NotBeNull($"{type.Name}: дедуп только среди живых задач");
            var inFilter = System.Text.RegularExpressions.Regex.Matches(filter!, @"\d+").Select(m => int.Parse(m.Value)).OrderBy(i => i).ToArray();
            inFilter.Should().Equal(expected, $"{type.Name}: фильтр уникального индекса обязан совпадать с EnrichmentJobStatusSets.Live (иначе дубль в очереди одобрения)");
        }
    }

    [Theory]
    [InlineData(EnrichmentJobStatus.AwaitingSearchApproval)]
    [InlineData(EnrichmentJobStatus.AwaitingResultReview)]
    public async Task UniqueIndex_RejectsSecondJobWithSameName_InAwaitingStatuses_ButAllowsAfterFailure(EnrichmentJobStatus status)
    {
        MedicationEnrichmentJob New(EnrichmentJobStatus s) => new()
        {
            Id = Guid.NewGuid(), NormalizedName = "ибупрофен", SourceDisplayName = "Ибупрофен", RequestedByUserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(), Status = s, CreatedAt = DateTime.UtcNow,
        };

        Db.MedicationEnrichmentJobs.Add(New(status));
        await Db.SaveChangesAsync();
        Db.MedicationEnrichmentJobs.Add(New(EnrichmentJobStatus.Pending));
        var duplicate = async () => await Db.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>("статусы 6/7 входят в частичный уникальный индекс");

        Db.ChangeTracker.Clear();
        var parked = await Db.MedicationEnrichmentJobs.SingleAsync();
        parked.Status = EnrichmentJobStatus.Failed;
        await Db.SaveChangesAsync();
        Db.MedicationEnrichmentJobs.Add(New(EnrichmentJobStatus.Pending));
        await Db.SaveChangesAsync();
    }
}
