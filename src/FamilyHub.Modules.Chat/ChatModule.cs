using FamilyHub.Infrastructure.Consents;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Modules.Chat.Chat;
using FamilyHub.Modules.Chat.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.Modules.Chat;

/// <summary>
/// Точка входа модуля «Чат семьи» (ADR-0019) — зависит только от Domain/Infrastructure. Медицинские
/// карточки ссылок приходят через IChatLinkResolver из Modules.Medical, вложения смотрятся существующими
/// эндпоинтами Medical (/api/attachments/...) — оба без прямой ссылки между модулями.
/// </summary>
public static class ChatModule
{
    public const string HubPath = "/hubs/chat";

    public static IServiceCollection AddChatModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.SectionName));
        services.AddScoped<ChatService>();
        services.AddScoped<ChatAttachmentStore>();
        services.AddScoped<ChatMessageNotifyJob>();
        services.AddScoped<IPushPayloadCustomizer, ChatPushCustomizer>();

        services.AddSignalR();
        services.AddSingleton<ChatPresenceTracker>();
        services.AddScoped<IChatEvents, ChatEvents>();
        return services;
    }

    public static void MapChatModule(this IEndpointRouteBuilder app)
    {
        // Сообщения семьи — ПДн (и на практике про здоровье) → та же консент-гарантия, что у Medical/Birthdays.
        // Хаб консент-фильтра не имеет, но войти в него можно только с тикетом, а тикет выдаёт эндпоинт под фильтром.
        app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>().MapChatEndpoints();
        app.MapHub<ChatHub>(HubPath);
    }
}
