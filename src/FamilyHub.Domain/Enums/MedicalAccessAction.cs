namespace FamilyHub.Domain.Enums;

/// <summary>Аудируемые действия с медицинскими данными (задача 2.7).</summary>
public enum MedicalAccessAction
{
    /// <summary>Просмотр списка записей, включавшего чужие (расшаренные) записи.</summary>
    ViewList = 0,

    /// <summary>Выдана ссылка на скачивание вложения (момент авторизации доступа к файлу).</summary>
    DownloadAttachment = 1,

    /// <summary>Владелец открыл семье доступ к своим записям.</summary>
    Share = 2,

    /// <summary>Владелец отозвал доступ семьи.</summary>
    Unshare = 3,

    /// <summary>Владелец скрыл запись от семей.</summary>
    Hide = 4,

    /// <summary>Владелец вернул видимость записи.</summary>
    Unhide = 5,

    /// <summary>Экспорт собственных данных субъектом.</summary>
    Export = 6,

    /// <summary>Удаление аккаунта (право на забвение).</summary>
    Erasure = 7,
}
