using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Members;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.Modules.Medical;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Конфигурация ---
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<LocalFileStorageOptions>(builder.Configuration.GetSection(LocalFileStorageOptions.SectionName));

// --- Persistence ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.")));

// --- Текущий пользователь / провижининг ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();

// --- Telegram auth ---
builder.Services.AddScoped<ITelegramInitDataValidator, TelegramInitDataValidator>();

// --- Хранилище файлов (временно локальное, заменяется на MinIO без изменений в вызывающем коде) ---
builder.Services.AddSingleton<LocalFileStorage>();
builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());

// --- Core-фичи: семьи, приглашения, участники ---
builder.Services.AddScoped<FamilyService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<MembershipService>();

// --- Авторизация по ролям в семье ---
builder.Services.AddScoped<IFamilyAccessService, FamilyAccessService>();
builder.Services.AddScoped<IAuthorizationHandler, FamilyRoleHandler>();
builder.Services.AddAuthorization(options =>
{
    // Защита по умолчанию: любой эндпоинт без явной политики всё равно требует аутентификации.
    options.FallbackPolicy = options.DefaultPolicy;
});

// --- Аутентификация: Telegram Mini App (прод) + Dev-заглушка (только Development) ---
var isDevelopment = builder.Environment.IsDevelopment();
var defaultScheme = isDevelopment ? "Smart" : AuthSchemes.TelegramMiniApp;

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = defaultScheme;
    options.DefaultAuthenticateScheme = defaultScheme;
    options.DefaultChallengeScheme = defaultScheme;
});

authBuilder.AddScheme<AuthenticationSchemeOptions, TelegramMiniAppAuthenticationHandler>(AuthSchemes.TelegramMiniApp, null);

if (isDevelopment)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(AuthSchemes.Dev, null);

    // Выбор схемы по заголовку: если пришёл X-Dev-TelegramId — используем Dev,
    // иначе обычную Telegram Mini App initData. Только для локальной разработки.
    authBuilder.AddPolicyScheme("Smart", "Smart", policyOptions =>
    {
        policyOptions.ForwardDefaultSelector = httpContext =>
            httpContext.Request.Headers.ContainsKey("X-Dev-TelegramId")
                ? AuthSchemes.Dev
                : AuthSchemes.TelegramMiniApp;
    });
}

// --- Medical-модуль ---
builder.Services.AddMedicalModule();

// --- Swagger (ручное тестирование) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

// --- Раздача файлов LocalFileStorage по подписанной ссылке (заглушка вместо MinIO presigned URL) ---
app.MapGet("/local-files/{*key}", (string key, long expires, string sig, LocalFileStorage storage) =>
{
    if (!storage.IsValidSignature(key, expires, sig))
        return Results.Unauthorized();

    var path = storage.ResolvePath(key);
    return File.Exists(path) ? Results.File(path) : Results.NotFound();
}).AllowAnonymous();

app.MapFamilyEndpoints();
app.MapInviteEndpoints();
app.MapMemberEndpoints();
app.MapMedicalModule();

app.Run();
