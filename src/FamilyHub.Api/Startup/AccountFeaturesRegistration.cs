using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Consents;
using FamilyHub.Infrastructure.Audit;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — согласия ПДн, права субъекта ПДн (удаление/
/// экспорт), профиль, привязка Telegram/слияние аккаунтов, аудит доступа к медданным — небольшие
/// смежные блоки "аккаунт и его данные", без изменения поведения/порядка.
/// </summary>
public static class AccountFeaturesRegistration
{
    public static WebApplicationBuilder AddFamilyHubAccountFeatures(this WebApplicationBuilder builder)
    {
        // --- Согласия ПДн (задача 2.3): версия + принятие + кэш для ConsentRequiredFilter ---
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<ConsentService>();

        // --- Права субъекта ПДн (задача 2.3): удаление аккаунта + экспорт ---
        builder.Services.AddScoped<AccountService>();

        // --- Профиль (identity rework): ФИО/ДР/пол после создания User ---
        builder.Services.AddScoped<ProfileService>();

        // --- Привязка Telegram к веб-аккаунту с подтверждением от бота + слияние аккаунтов ---
        builder.Services.AddScoped<AccountMergeService>();
        builder.Services.AddScoped<TelegramLinkService>();

        // --- Аудит доступа к медданным (задача 2.7): синхронная запись + ретеншн-джоба ---
        builder.Services.AddScoped<IMedicalAuditWriter, MedicalAuditWriter>();
        builder.Services.AddScoped<AuditRetentionJob>();

        return builder;
    }
}
