using System.Net;
using System.Text;
using System.Text.Json;
using FamilyHub.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>
/// Минимальный клиент admin API MinIO ровно под три операции ротации service account'а (ADR-0011):
/// add-service-account, delete-service-account, info-service-account. Запросы подписываются
/// AwsSigV4Signer текущими кредами приложения, тело add-service-account шифруется
/// MadminPayloadCipher. Ответы, которые сервер шифрует (Argon2id), НЕ читаются: access/secret key
/// выбирает приложение само (SecretGenerator), сервер лишь подтверждает статусом.
/// </summary>
public sealed class MinioAdminClient(HttpClient http, IOptions<MinioOptions> options) : IMinioCredentialAdmin
{
    private const string AdminPrefix = "/minio/admin/v3";
    // Как madmin-go: пустой регион у подписи превращается в us-east-1, сервис — s3.
    private const string Region = "us-east-1";
    private const string Service = "s3";

    public string CurrentAccessKey => options.Value.AccessKey;

    public async Task<MinioCredentialMode> GetModeAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Get, "/info-service-account", $"accessKey={Uri.EscapeDataString(options.Value.AccessKey)}", null, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return MinioCredentialMode.Unknown;
        }

        using (response)
        {
            // 200 — «свой» service account существует: работаем под ним. 4xx (для root/обычного
            // пользователя MinIO отвечает «service account not found») — не service account.
            if (response.StatusCode == HttpStatusCode.OK) return MinioCredentialMode.ServiceAccount;
            return (int)response.StatusCode < 500 ? MinioCredentialMode.NotServiceAccount : MinioCredentialMode.Unknown;
        }
    }

    public async Task CreateServiceAccountAsync(string accessKey, string secretKey, string name, CancellationToken ct = default)
    {
        // targetUser не задаём: service account создаётся под родителем вызывающего.
        var json = JsonSerializer.SerializeToUtf8Bytes(new { accessKey, secretKey, name, description = "FamilyHub application credentials (rotated from the admin panel)" });
        var body = MadminPayloadCipher.Encrypt(options.Value.SecretKey, json);

        using var response = await SendOrThrowAsync(HttpMethod.Put, "/add-service-account", null, body, ct);
        await EnsureAsync(response, "add-service-account", acceptNotFound: false, ct);
    }

    public async Task DeleteServiceAccountAsync(string accessKey, CancellationToken ct = default)
    {
        using var response = await SendOrThrowAsync(HttpMethod.Delete, "/delete-service-account", $"accessKey={Uri.EscapeDataString(accessKey)}", null, ct);
        await EnsureAsync(response, "delete-service-account", acceptNotFound: true, ct);
    }

    private async Task<HttpResponseMessage> SendOrThrowAsync(HttpMethod method, string path, string? query, byte[]? body, CancellationToken ct)
    {
        try
        {
            return await SendAsync(method, path, query, body, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new CredentialAdminException(CredentialAdminError.Unavailable, "MinIO недоступен.", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? query, byte[]? body, CancellationToken ct)
    {
        var o = options.Value;
        var uri = new Uri($"{(o.UseSsl ? "https" : "http")}://{o.Endpoint}{AdminPrefix}{path}{(query is null ? string.Empty : "?" + query)}");
        var payload = body ?? [];
        var payloadHash = AwsSigV4Signer.Sha256Hex(payload);
        var now = DateTime.UtcNow;
        var amzDate = AwsSigV4Signer.AmzDate(now);

        var signed = new Dictionary<string, string>
        {
            ["host"] = uri.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Host = uri.Authority;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            AwsSigV4Signer.Authorization(method.Method, uri, signed, payloadHash, o.AccessKey, o.SecretKey, Region, Service, now));
        if (body is not null) request.Content = new ByteArrayContent(body);

        // HttpResponseMessage возвращается вызывающему: его Dispose — на нём (request освобождается здесь).
        return await http.SendAsync(request, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string operation, bool acceptNotFound, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode || (acceptNotFound && response.StatusCode == HttpStatusCode.NotFound)) return;

        // Тело ошибки MinIO — открытый XML/JSON (не зашифрован). Берём короткий фрагмент для диагностики.
        var text = await response.Content.ReadAsStringAsync(ct);
        var snippet = text.Length > 300 ? text[..300] : text;
        var kind = (int)response.StatusCode >= 500 ? CredentialAdminError.Unavailable : CredentialAdminError.Rejected;
        throw new CredentialAdminException(kind, $"MinIO admin API {operation}: HTTP {(int)response.StatusCode} {snippet}".TrimEnd());
    }
}
