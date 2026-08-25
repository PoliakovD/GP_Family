using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Реализация IFileStorage на базе MinIO (этап 2 п.9 брифа). С этапа 2 (152-ФЗ) в бакете
/// лежит только шифротекст (IFileCipher), а скачивание идёт через API-эндпоинт с
/// расшифровкой — presigned-ссылки на бакет упразднены (они отдавали бы шифротекст).
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

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        // Вложения ограничены лимитом загрузки — буферизация в памяти приемлема
        // (расшифровка IFileCipher всё равно буферизует блоб целиком).
        var buffer = new MemoryStream();
        await minioClient.GetObjectAsync(new GetObjectArgs()
            .WithBucket(options.Value.Bucket)
            .WithObject(storageKey)
            .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(buffer, token)), ct);

        buffer.Position = 0;
        return buffer;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        // RemoveObject у MinIO идемпотентен: удаление отсутствующего объекта не бросает.
        await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(options.Value.Bucket)
            .WithObject(storageKey), ct);
    }

    public async IAsyncEnumerable<StorageObjectInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        var args = new ListObjectsArgs().WithBucket(options.Value.Bucket).WithRecursive(true);
        await foreach (var item in minioClient.ListObjectsEnumAsync(args, ct))
        {
            if (item.IsDir) continue;
            yield return new StorageObjectInfo(item.Key, (long)item.Size);
        }
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
