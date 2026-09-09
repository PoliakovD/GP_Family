namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Технический (не смысловой) сбой обращения к LM Studio — сервер недоступен, таймаут
/// или 5xx/429 (см. <see cref="LmStudioJsonResult.IsTransient"/>). Пробрасывается процессорами
/// конвейера ВМЕСТО терминального Failed на гейте/распознавании, чтобы обычный catch(Exception) +
/// уже настроенный [AutomaticRetry] реально повторил задачу, а не похоронил её с первой попытки
/// (LmStudioJsonClient раньше возвращал такой сбой как обычную бизнес-ошибку, не исключение — см.
/// план, часть 1). Отдельный тип нужен только чтобы на исчерпании попыток проставить
/// Job.IsTransientFailure=true — по нему LmStudioRecoverySweepJob находит задачи, которые стоит
/// вернуть в очередь, когда сервер снова станет доступен.</summary>
public class LmStudioUnavailableException(string message) : Exception(message);
