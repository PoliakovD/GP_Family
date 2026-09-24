namespace FamilyHub.Domain.Enums;

/// <summary>Стадия одной ротации учётки приложения (ADR-0011). Жизненный цикл линейный:
/// AwaitingDeploy → Activated → Revoked; Superseded — ротация заменена новой до активации.</summary>
public enum CredentialRotationStatus
{
    /// <summary>Новая учётка выпущена и показана администратору, но приложение ещё работает под старой
    /// (пароль/ключ ждёт, пока его положат в PROD_ENV и задеплоят). Не более одной на вид учётки.</summary>
    AwaitingDeploy = 0,

    /// <summary>Приложение перезапущено и работает под новой учёткой; старая ещё действует.</summary>
    Activated = 1,

    /// <summary>Старая учётка отозвана — ротация завершена.</summary>
    Revoked = 2,

    /// <summary>Заменена новой ротацией до того, как приложение перешло на эту учётку (например,
    /// секрет потеряли и сгенерировали заново).</summary>
    Superseded = 3,
}
