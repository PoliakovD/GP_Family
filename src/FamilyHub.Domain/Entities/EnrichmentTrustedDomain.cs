using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Доверенный домен для одного из конвейеров обогащения (Medication/LabAnalyte), управляемый через
/// админку — раньше это был статический массив в EnrichmentOptions (appsettings), теперь строка в
/// БД: включение/выключение домена и переупорядочивание приоритета (Rank — значим только для
/// LabAnalyte, см. ReferenceRangeMerger) не требуют передеплоя. IsEnabled=false — домен временно
/// исключён без удаления истории/ранга. Сама фильтрация переехала с провайдера (BraveSearchProvider/
/// YandexSearchProvider больше не отбрасывают недоверенные результаты — кэш хранит ВСЕ сниппеты,
/// которые вернул поиск) на процессор обогащения (см. EnrichmentSnippetFilter) — так админ может
/// поменять список доверенных доменов и переиграть уже закэшированные сырые результаты без нового
/// платного запроса.
/// </summary>
public class EnrichmentTrustedDomain
{
    public Guid Id { get; set; }

    public WebSearchTopic Topic { get; set; }

    public string Domain { get; set; } = string.Empty;

    /// <summary>Приоритет при конфликте источников (0 — самый приоритетный) — используется только
    /// для Topic=LabAnalyte (ReferenceRangeMerger); для Medication порядок не имеет значения, но
    /// поле всё равно заполняется (порядок добавления), чтобы не заводить два разных набора полей.</summary>
    public int Rank { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
