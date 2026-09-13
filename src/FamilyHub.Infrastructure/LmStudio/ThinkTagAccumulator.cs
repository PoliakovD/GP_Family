using System.Text;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Извлекает текст ВНУТРИ ещё не закрытого &lt;think&gt;...&lt;/think&gt; по мере поступления
/// потоковых delta-кусков ответа модели (см. LmStudioJsonClient — живой поток "мыслей", план
/// "живой поток мыслей модели"). Пересобирает результат каждый раз ЦЕЛИКОМ из накопленного
/// буфера, а не char-by-char стейт-машиной — открывающий/закрывающий тег может разбиться по
/// границе чанков в любом месте, а полная пересборка маленькой (единицы КБ на весь ответ) строки
/// на каждый чанк надёжнее, чем гоняться за граничными случаями, и обходится дешевле, чем кажется
/// (вызывается не на каждый токен, а раз в throttle-интервал репортера).
///
/// Финальная разборка ответа (снятие &lt;think&gt;/markdown-фенсов/JSON, см.
/// LmStudioJsonClient.ExtractJsonPayload) не меняется вообще — работает на FullContent, полном
/// накопленном тексте после того, как стрим завершился, точно так же, как сегодня на
/// нестриминговом ответе. Этот класс отвечает только за то, что показать пользователю ПОКА модель
/// ещё думает.
///
/// Предполагается не более одного &lt;think&gt;-блока за ответ (реальное поведение Qwen3-семейства
/// — один блок рассуждений перед финальным ответом, не несколько) — второй блок, если он всё же
/// появится, будет проигнорирован (IndexOf ищёт первое вхождение).
/// </summary>
public sealed class ThinkTagAccumulator
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private readonly StringBuilder _raw = new();

    /// <summary>Весь накопленный текст, как он есть — то же самое, что вернул бы Stream: false
    /// одним куском.</summary>
    public string FullContent => _raw.ToString();

    /// <summary>Добавляет очередной delta.content и возвращает текст, накопленный внутри ещё не
    /// закрытого &lt;think&gt; — null, если тег ещё не открылся или уже закрылся (в последнем
    /// случае "думать" уже нечего — модель перешла к финальному ответу; вызывающий код в этот
    /// момент обычно очищает "текущую мысль", см. LlmThinkingReportService.ClearAsync).</summary>
    public string? AppendDelta(string? delta)
    {
        if (!string.IsNullOrEmpty(delta)) _raw.Append(delta);
        return CurrentThinking;
    }

    private string? CurrentThinking
    {
        get
        {
            var text = _raw.ToString();
            var openIdx = text.IndexOf(OpenTag, StringComparison.Ordinal);
            if (openIdx < 0) return null;

            var contentStart = openIdx + OpenTag.Length;
            var closeIdx = text.IndexOf(CloseTag, contentStart, StringComparison.Ordinal);
            return closeIdx < 0 ? text[contentStart..] : null;
        }
    }
}
