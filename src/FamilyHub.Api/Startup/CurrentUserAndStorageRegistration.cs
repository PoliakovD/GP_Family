using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Minio;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — текущий пользователь/провижининг,
/// Telegram-аутентификация и файловое хранилище (MinIO), без изменения поведения/порядка. Три
/// небольших, но самостоятельных секции объединены в один файл — каждая по отдельности была
/// слишком мала (3-6 строк) для собственного файла, но проще не удерживать их прямо в Program.cs.
/// </summary>
public static class CurrentUserAndStorageRegistration
{
    public static WebApplicationBuilder AddFamilyHubCurrentUser(this WebApplicationBuilder builder)
    {
        // --- Текущий пользователь / провижининг ---
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();

        // --- Telegram auth ---
        builder.Services.AddScoped<ITelegramInitDataValidator, TelegramInitDataValidator>();

        return builder;
    }

    public static WebApplicationBuilder AddFamilyHubFileStorage(this WebApplicationBuilder builder)
    {
        // --- Хранилище файлов: MinIO — единственная реализация IFileStorage, в т.ч. в Development ---
        // Раньше был переключатель FileStorage:Provider = Local|Minio: запуск из IDE тихо писал
        // медицинские сканы на диск мимо объектного хранилища, и этот путь никогда не проверялся.
        // Fail-fast на пустые креды — MinioOptionsValidator (AddOptions<MinioOptions>().ValidateOnStart()
        // в AddFamilyHubOptions), не ручной guard здесь — без него ошибка всплыла бы только при
        // первой загрузке файла.
        builder.Services.AddSingleton<IMinioClient>(sp =>
        {
            var minioOptions = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
            return (IMinioClient)new MinioClient()
                .WithEndpoint(minioOptions.Endpoint)
                .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
                .WithSSL(minioOptions.UseSsl)
                .Build();
        });
        builder.Services.AddSingleton<IFileStorage, MinioFileStorage>();

        return builder;
    }
}
