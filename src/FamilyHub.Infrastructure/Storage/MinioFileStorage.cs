using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Реальная реализация IFileStorage на базе MinIO (этап 2 п.9 брифа). Заменяет LocalFileStorage
/// без изменений в вызывающем коде (AttachmentService и т.п. работают только через абстракцию).
/// Доступ к файлам — исключительно через короткоживущие presigned GET URL (раздел 9 брифа),
/// постоянных прямых ссылок на бакет не существует.
/// </summary>
public class MinioFileStorage(IMinioClient minioClient, IOptions<MinioOptions> options) : IFileStorage
{
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketEnsured;

    public async Task<string> SaveAsync(string storageKey, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        await minioClient.PutObjectAsync(new PutObjectArgs()
            .WithBucket(options.Value.Bucket)
            .WithObject(storageKey)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType), ct);

        return storageKey;
    }

    public async Task<string> GetPresignedUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default)
    {
        // PresignedGetObjectAsync не принимает CancellationToken — сам запрос к MinIO за подписью
        // короткий (без сетевого обращения за данными файла), поэтому ct здесь не нужен.
        var url = await minioClient.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(options.Value.Bucket)
            .WithObject(storageKey)
            .WithExpiry((int)expiry.TotalSeconds));

        if (string.IsNullOrEmpty(options.Value.PublicEndpoint))
            return url;

        // Подменяем хост на публично доступный (домашний ПК виден изнутри по одному адресу,
        // клиентам — по другому, через туннель/прокси), сохраняя подписанные query-параметры.
        var uri = new Uri(url);
        var publicUri = new UriBuilder(uri)
        {
            Host = new Uri($"{(options.Value.UseSsl ? "https" : "http")}://{options.Value.PublicEndpoint}").Host,
        };
        return publicUri.Uri.ToString();
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;

        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_bucketEnsured) return;

            var bucket = options.Value.Bucket;
            var found = await minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
            if (!found)
                await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);

            _bucketEnsured = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }
}
