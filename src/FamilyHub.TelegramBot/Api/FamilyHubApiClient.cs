using System.Net.Http.Json;
using FamilyHub.Contracts.BotApi;

namespace FamilyHub.TelegramBot.Api;

/// <summary>
/// Простой типизированный HttpClient (не Refit) — четыре вызова, один content-type, ни
/// query-строк, ни auth-флоу; тот же приём, что уже дважды используется в FamilyHub.Api
/// (LmStudioJsonClient, BraveSearchProvider — AddHttpClient&lt;TInterface, TImpl&gt;).
/// </summary>
public class FamilyHubApiClient(HttpClient http) : IFamilyHubApiClient
{
    public Task<ResolveUserResponse> ResolveUserAsync(long telegramId, CancellationToken ct) =>
        PostAsync<ResolveUserRequest, ResolveUserResponse>("/internal/bot/users/resolve", new(telegramId), ct);

    public Task<RedeemInviteResponse> RedeemInviteAsync(string code, long telegramId, CancellationToken ct) =>
        PostAsync<RedeemInviteRequest, RedeemInviteResponse>("/internal/bot/invites/redeem", new(code, telegramId), ct);

    public Task<PeekLinkResponse> PeekTelegramLinkAsync(string code, CancellationToken ct) =>
        PostAsync<PeekLinkRequest, PeekLinkResponse>("/internal/bot/telegram-link/peek", new(code), ct);

    public Task<ConfirmLinkResponse> ConfirmTelegramLinkAsync(
        string code, long telegramId, string? displayName, string? username, CancellationToken ct) =>
        PostAsync<ConfirmLinkRequest, ConfirmLinkResponse>(
            "/internal/bot/telegram-link/confirm", new(code, telegramId, displayName, username), ct);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, request, ct);
            if (!response.IsSuccessStatusCode)
                throw new FamilyHubApiUnavailableException(
                    $"FamilyHub.Api ответил {(int)response.StatusCode} на {path}.");

            var body = await response.Content.ReadFromJsonAsync<TResponse>(ct);
            return body ?? throw new FamilyHubApiUnavailableException($"FamilyHub.Api вернул пустое тело на {path}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FamilyHubApiUnavailableException($"FamilyHub.Api недоступен ({path}).", ex);
        }
    }
}
