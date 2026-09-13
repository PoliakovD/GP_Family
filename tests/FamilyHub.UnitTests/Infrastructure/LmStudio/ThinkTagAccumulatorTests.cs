using FamilyHub.Infrastructure.LmStudio;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.LmStudio;

/// <summary>
/// Главный риск плана "живой поток мыслей" — &lt;think&gt;/&lt;/think&gt; может разбиться по
/// границе SSE-чанков в произвольном месте (LM Studio отдаёт токены, не строки), и класс должен
/// пересобирать состояние из полного накопленного буфера на каждый вызов, а не терять открытие/
/// закрытие тега из-за того, что он не попал целиком в один AppendDelta.
/// </summary>
public class ThinkTagAccumulatorTests
{
    [Fact]
    public void AppendDelta_NoThinkTagAtAll_AlwaysReturnsNull()
    {
        var sut = new ThinkTagAccumulator();

        sut.AppendDelta("{\"valid\"").Should().BeNull();
        sut.AppendDelta(": true}").Should().BeNull();

        sut.FullContent.Should().Be("{\"valid\": true}");
    }

    [Fact]
    public void AppendDelta_TagArrivesWhole_ReturnsAccumulatedThinking()
    {
        var sut = new ThinkTagAccumulator();

        sut.AppendDelta("<think>Читаю").Should().Be("Читаю");
        sut.AppendDelta(" таблицу").Should().Be("Читаю таблицу");
    }

    [Fact]
    public void AppendDelta_OpenTagSplitAcrossChunkBoundary_StillDetectsOpening()
    {
        var sut = new ThinkTagAccumulator();

        // "<think>" разорван прямо посередине — реалистичный случай для токенового стрима.
        sut.AppendDelta("<th").Should().BeNull("тег еще не закрылся целиком, открытия не видно");
        var result = sut.AppendDelta("ink>Читаю таблицу");

        result.Should().Be("Читаю таблицу");
    }

    [Fact]
    public void AppendDelta_CloseTagSplitAcrossChunkBoundary_StopsReportingOnceComplete()
    {
        var sut = new ThinkTagAccumulator();

        sut.AppendDelta("<think>Готово");
        sut.AppendDelta("</th").Should().Be(
            "Готово</th", "закрывающий тег ещё не закрылся целиком — то, что накопилось, всё ещё внутри <think>");

        var result = sut.AppendDelta("ink>{\"valid\":true}");

        result.Should().BeNull("</think> теперь закрылся целиком — думать больше нечего");
    }

    [Fact]
    public void AppendDelta_AfterThinkClosed_SubsequentDeltasReturnNull()
    {
        var sut = new ThinkTagAccumulator();

        sut.AppendDelta("<think>мысль</think>");
        sut.AppendDelta("{\"valid\":true}").Should().BeNull();
    }

    [Fact]
    public void AppendDelta_ThinkNeverCloses_KeepsReturningAccumulatedThinking()
    {
        // Ответ обрывается (сеть/таймаут) до </think> — вызывающий код (SendStreamingChatCompletionAsync)
        // в этот момент всё ещё должен видеть последнюю накопленную мысль, а не null.
        var sut = new ThinkTagAccumulator();

        sut.AppendDelta("<think>Читаю");
        var result = sut.AppendDelta(" бланк, показатель за показателем");

        result.Should().Be("Читаю бланк, показатель за показателем");
    }

    [Fact]
    public void AppendDelta_NullOrEmptyDelta_DoesNotThrow_ReturnsCurrentState()
    {
        var sut = new ThinkTagAccumulator();
        sut.AppendDelta("<think>уже думаю");

        sut.AppendDelta(null).Should().Be("уже думаю");
        sut.AppendDelta("").Should().Be("уже думаю");
    }

    [Fact]
    public void FullContent_ReturnsEverythingIncludingThinkTags_RegardlessOfThinkState()
    {
        var sut = new ThinkTagAccumulator();
        sut.AppendDelta("<think>рассуждение</think>");
        sut.AppendDelta("{\"valid\":true}");

        // FullContent — сырой вход для существующего (не тронутого этим планом) ExtractJsonPayload,
        // который сам умеет вырезать <think> регэкспом — здесь ничего заранее не вырезаем.
        sut.FullContent.Should().Be("<think>рассуждение</think>{\"valid\":true}");
    }

    [Fact]
    public void AppendDelta_SecondThinkBlock_IsIgnored_OnlyFirstOneTracked()
    {
        // Задокументированное ограничение (см. class doc) — Qwen3-семейство отдаёт не больше
        // одного think-блока за ответ на практике, второй (если он всё же появится) не отследится.
        var sut = new ThinkTagAccumulator();
        sut.AppendDelta("<think>первая</think>{\"a\":1}<think>вторая");

        sut.AppendDelta("").Should().BeNull("первый think уже закрылся — IndexOf находит именно его");
    }
}
