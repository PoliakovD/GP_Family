using System.Reflection;
using FamilyHub.Domain;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Security.Rotation;

/// <summary>
/// Фоновая перешифровка данных активным ключом после ротации (ADR-0009). Запускается либо
/// вручную из админ-панели (AdminKeysService.StartRotationAsync создаёт/находит строку
/// EncryptionRotationRun и ставит эту джобу в очередь Hangfire "rotation", один воркер — см.
/// Program.cs), либо ночным добивателем ("encryption-rotation-catchup"), который НИКОГДА не
/// создаёт новый прогон сам — только резюмирует уже идущий (Status=Running), если предыдущее
/// исполнение оборвалось (рестарт контейнера, транзиентный сбой сети до MinIO/Postgres).
///
/// Единственный воркер очереди "rotation" — гарантия, что одновременно исполняется не больше
/// одной джобы: ручной клик "Перешифровать" и ночной тик просто ставят вызовы в одну очередь
/// подряд, конкурентной записи в EncryptionRotationRun не возникает.
///
/// Две фазы, каждая резюмируема курсором в самой строке EncryptionRotationRun (переживает
/// рестарт процесса — не in-memory состояние):
/// 1. Поля — <see cref="FieldEntityTypes"/>, обход постранично, помечает [Encrypted]-свойства
///    IsModified и пересохраняет. Не проверяет, на каком именно ключе сейчас конкретное
///    значение (для этого пришлось бы читать и парсить сырой шифротекст в обход
///    IFieldCipher) — просто перезаписывает КАЖДУЮ строку активным ключом. Значение, уже
///    бывшее на активном ключе, получает новый nonce при повторной записи — расточительно
///    при пустой ротации, но безвредно и многократно проще спец-детектора "что именно устарело".
/// 2. Блобы вложений — фильтруется по денормализованной FileAttachment.KeyId (дешёвый SQL
///    WHERE вместо скачивания каждого объекта из MinIO ради заголовка).
/// </summary>
[Queue("rotation")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class EncryptionRotationJob(
    AppDbContext db,
    IEncryptionKeyRing keyRing,
    IFileStorage storage,
    IFileCipher fileCipher,
    ILogger<EncryptionRotationJob> logger)
{
    private const int PageSize = 200;

    /// <summary>
    /// Типы сущностей с [Encrypted]-свойствами, в фиксированном порядке — резюмируемый курсор
    /// (EncryptionRotationRun.FieldsStepIndex) адресует позицию именно в ЭТОМ списке, поэтому он
    /// НЕ авто-обнаруживается сканом модели (в отличие от AppDbContext.OnModelCreating, который
    /// навешивает конвертер на любое такое свойство). Добавляя новую [Encrypted]-сущность —
    /// дописать её сюда; забытую запись ловит EncryptionRotationJobTests.
    /// FieldEntityTypes_CoversEveryEncryptedEntityInModel.
    /// </summary>
    public static readonly IReadOnlyList<Type> FieldEntityTypes =
    [
        typeof(MedicalRecord), typeof(Birthday), typeof(FamilyDependent),
        typeof(PushSubscription), typeof(FileAttachment),
    ];

    public async Task RunAsync(CancellationToken ct = default)
    {
        var run = await db.EncryptionRotationRuns
            .FirstOrDefaultAsync(r => r.Status == EncryptionRotationStatus.Running, ct);
        if (run is null)
        {
            logger.LogInformation("EncryptionRotationJob: нет прогона в статусе Running — нечего делать.");
            return;
        }

        try
        {
            await RotateFieldsAsync(run, ct);
            if (!await IsCancelledAsync(run.Id, ct))
                await RotateBlobsAsync(run, ct);

            var cancelled = await IsCancelledAsync(run.Id, ct);
            run.Status = cancelled ? EncryptionRotationStatus.Cancelled : EncryptionRotationStatus.Completed;
            run.CancelRequested = cancelled;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("EncryptionRotationJob {RunId}: завершён со статусом {Status}.", run.Id, run.Status);
        }
        catch (OperationCanceledException)
        {
            // Отменён извне (остановка хоста) — прогон остаётся Running: следующий вызов
            // (ручной или ночной добиватель) продолжит с сохранённого курсора, не с начала.
            throw;
        }
        catch (Exception ex)
        {
            // Статус НЕ переводится в Failed: оставляем Running, чтобы ночной добиватель
            // (или следующий клик "Перешифровать") сам подхватил прогон с последнего сохранённого
            // курсора. LastError — только диагностика для админки; Hangfire отдельно ретраит сам
            // вызов джобы (AutomaticRetry) и покажет историю сбоев в своём дашборде.
            run.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "EncryptionRotationJob {RunId} упал — прогон остаётся Running для резюме.", run.Id);
            throw;
        }
    }

    // --- Фаза 1: поля ---

    private async Task RotateFieldsAsync(EncryptionRotationRun run, CancellationToken ct)
    {
        while (run.FieldsStepIndex < FieldEntityTypes.Count)
        {
            if (await IsCancelledAsync(run.Id, ct)) return;

            await RotateFieldStepAsync(run.FieldsStepIndex, run, ct);

            if (await IsCancelledAsync(run.Id, ct)) return;

            // Дошли до конца текущего типа без отмены — переходим к следующему.
            run.FieldsStepIndex++;
            run.FieldsCursorId = null;
            await db.SaveChangesAsync(ct);
        }
    }

    private static readonly MethodInfo RotateEntityAsyncDefinition = typeof(EncryptionRotationJob)
        .GetMethod(nameof(RotateEntityAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Диспетчер по индексу в FieldEntityTypes — единственный источник истины: раньше здесь был
    /// ручной switch по номеру шага, который однажды разошёлся со списком (новая запись в
    /// FieldEntityTypes без соответствующей ветки switch давала рантайм-исключение вместо ошибки
    /// компиляции). MakeGenericMethod дороже статического вызова, но это один раз за шаг (не за
    /// страницу/строку) — цена незаметна на фоне сетевых вызовов MinIO/Postgres в самом прогоне.
    /// </summary>
    private Task RotateFieldStepAsync(int stepIndex, EncryptionRotationRun run, CancellationToken ct)
    {
        var entityType = FieldEntityTypes[stepIndex];
        var typedMethod = RotateEntityAsyncDefinition.MakeGenericMethod(entityType);
        return (Task)typedMethod.Invoke(this, [run, ct])!;
    }

    private async Task RotateEntityAsync<T>(EncryptionRotationRun run, CancellationToken ct) where T : class
    {
        var entityType = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} не зарегистрирован в модели AppDbContext.");
        var encryptedProperties = entityType.GetProperties()
            .Where(p => p.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is not null)
            .Select(p => p.Name)
            .ToList();
        if (encryptedProperties.Count == 0)
            throw new InvalidOperationException(
                $"{typeof(T).Name} перечислен в FieldEntityTypes, но не имеет ни одного [Encrypted]-свойства.");
        // EF.Property<T> — маркер, транслируемый только ВНУТРИ LINQ-выражения (см. OrderBy/Where
        // ниже); на уже материализованном CLR-объекте (page[^1]) он бросает — курсор после
        // страницы читаем обычной reflection.
        var idProperty = typeof(T).GetProperty("Id")
            ?? throw new InvalidOperationException($"{typeof(T).Name} не имеет свойства Id.");

        if (run.FieldsCursorId is null)
        {
            var total = await db.Set<T>().CountAsync(ct);
            run.FieldsTotal += total;
            await db.SaveChangesAsync(ct);
        }

        while (true)
        {
            if (await IsCancelledAsync(run.Id, ct)) return;
            ct.ThrowIfCancellationRequested();

            var cursor = run.FieldsCursorId;
            var page = await db.Set<T>()
                .OrderBy(e => EF.Property<Guid>(e, "Id"))
                .Where(e => cursor == null || EF.Property<Guid>(e, "Id") > cursor)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0) return;

            foreach (var entity in page)
            {
                var entry = db.Entry(entity);
                foreach (var propertyName in encryptedProperties)
                    entry.Property(propertyName).IsModified = true;
            }

            run.FieldsCursorId = (Guid)idProperty.GetValue(page[^1])!;
            run.FieldsProcessed += page.Count;
            await db.SaveChangesAsync(ct);
            ClearTrackerKeepingRun(run);

            if (page.Count < PageSize) return;
        }
    }

    // --- Фаза 2: блобы вложений ---

    private async Task RotateBlobsAsync(EncryptionRotationRun run, CancellationToken ct)
    {
        if (run.BlobsCursorId is null)
        {
            run.BlobsTotal = await db.FileAttachments
                .CountAsync(a => a.IsEncrypted && a.KeyId != keyRing.ActiveKeyId, ct);
            await db.SaveChangesAsync(ct);
        }

        while (true)
        {
            if (await IsCancelledAsync(run.Id, ct)) return;
            ct.ThrowIfCancellationRequested();

            var cursor = run.BlobsCursorId;
            var page = await db.FileAttachments
                .Where(a => a.IsEncrypted && a.KeyId != keyRing.ActiveKeyId)
                .Where(a => cursor == null || a.Id > cursor)
                .OrderBy(a => a.Id)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0) return;

            foreach (var attachment in page)
            {
                ct.ThrowIfCancellationRequested();

                Stream plain;
                await using (var stored = await storage.OpenReadAsync(attachment.StorageKey, ct))
                {
                    plain = await fileCipher.DecryptAsync(stored, ct);
                }
                await using (plain)
                {
                    using var reencrypted = new MemoryStream();
                    var size = await fileCipher.EncryptAsync(plain, reencrypted, ct);
                    reencrypted.Position = 0;
                    // Перезаливка под ТЕМ ЖЕ StorageKey — блоб не переезжает, только его содержимое.
                    await storage.SaveAsync(attachment.StorageKey, reencrypted, size, "application/octet-stream", ct);
                }

                attachment.KeyId = keyRing.ActiveKeyId;
            }

            run.BlobsCursorId = page[^1].Id;
            run.BlobsProcessed += page.Count;
            await db.SaveChangesAsync(ct);
            ClearTrackerKeepingRun(run);

            if (page.Count < PageSize) return;
        }
    }

    /// <summary>
    /// Перечитывает CancelRequested напрямую из БД, а не из локальной копии <paramref name="run"/> в
    /// памяти джобы: отмена ставится отдельным запросом администратора (AdminKeysService,
    /// другой DbContext/scope) и не отражается в уже загруженном сюда экземпляре сама по себе.
    /// </summary>
    private async Task<bool> IsCancelledAsync(Guid runId, CancellationToken ct) =>
        await db.EncryptionRotationRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.CancelRequested)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// ChangeTracker.Clear() открепляет ВСЕ отслеживаемые сущности, включая сам <paramref name="run"/>
    /// (загружен один раз в начале RunAsync, живёт весь прогон) — без повторного Attach следующее
    /// присвоение run.FieldsCursorId/BlobsCursorId перестало бы попадать в SaveChangesAsync, и
    /// курсор молча замер бы на значении первой страницы навсегда.
    /// </summary>
    private void ClearTrackerKeepingRun(EncryptionRotationRun run)
    {
        db.ChangeTracker.Clear();
        db.Attach(run);
    }
}
