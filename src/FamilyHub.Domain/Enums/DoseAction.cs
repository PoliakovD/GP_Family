namespace FamilyHub.Domain.Enums;

/// <summary>Действие над приёмом — из приложения или кнопки push-уведомления.</summary>
public enum DoseAction
{
    Taken = 0,
    Snooze10 = 1,
    Snooze30 = 2,
    Skip = 3,
}
