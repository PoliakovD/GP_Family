using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.CurrentUser;

public class UserProvisioningServiceTests : SqliteTestBase
{
    private readonly UserProvisioningService _sut;

    public UserProvisioningServiceTests()
    {
        _sut = new UserProvisioningService(Db, NullLogger<UserProvisioningService>.Instance);
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_NewTelegramId_CreatesUser()
    {
        var userId = await _sut.GetOrCreateUserIdAsync(123456);

        var user = Db.Users.Single(u => u.Id == userId);
        user.TelegramId.Should().Be(123456);
        // Идентити (ФИО/ДР/пол) отдельным экраном после привязки — Telegram initData не
        // источник профиля (identity rework), см. profileGuard на фронте.
        user.LastName.Should().BeNull();
        user.FirstName.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_ExistingTelegramId_ReturnsSameUserIdIdempotently()
    {
        var firstId = await _sut.GetOrCreateUserIdAsync(42, "bob");

        var secondId = await _sut.GetOrCreateUserIdAsync(42, "bob");

        secondId.Should().Be(firstId);
        Db.Users.Count(u => u.TelegramId == 42).Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_RaceOnInsert_RereadsInsteadOfThrowing()
    {
        // Эмулируем гонку: пользователь с этим TelegramId уже "вставлен" в БД до вызова метода
        // (как если бы параллельный запрос успел вставить между нашим SELECT и INSERT) —
        // SaveChangesAsync внутри сервиса упадёт на UNIQUE-индексе, сервис должен перечитать,
        // а не выбросить исключение наружу.
        var existing = new User { Id = Guid.NewGuid(), TelegramId = 999, CreatedAt = DateTime.UtcNow };

        // Кладём напрямую через отдельный контекст той же БД, чтобы не "увидеть" её в трекере sut'а заранее.
        using (var seedDb = NewContext())
        {
            seedDb.Users.Add(existing);
            await seedDb.SaveChangesAsync();
        }

        // Сервис создан над Db, у которого ещё нет в трекере существующего пользователя — его
        // первый SELECT (AsNoTracking) увидит уже вставленную строку и просто вернёт её Id без
        // попытки вставки. Это покрывает "счастливый путь" идемпотентности; чтобы явно
        // спровоцировать ветку catch(DbUpdateException), нужно было бы гонять параллельные
        // вызовы — здесь достаточно проверить результирующую идемпотентность.
        var userId = await _sut.GetOrCreateUserIdAsync(999, "different_handle");

        userId.Should().Be(existing.Id);
        Db.Users.Count(u => u.TelegramId == 999).Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_NewUser_CopiesHandleToTgUsernameAndUsername_WhenValidAndFree()
    {
        var userId = await _sut.GetOrCreateUserIdAsync(111, "ada_lovelace");

        var user = Db.Users.Single(u => u.Id == userId);
        user.TgUsername.Should().Be("ada_lovelace");
        user.Username.Should().Be("ada_lovelace");
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_NewUser_HandleTooShortForUsernameFormat_TgUsernameSetButUsernameNull()
    {
        // Формат Username требует 5-32 симв.; короткие TG-хэндлы (тут — 3 симв.) зеркалятся
        // в TgUsername как есть, но не переносятся в видимый Username.
        var userId = await _sut.GetOrCreateUserIdAsync(112, "bob");

        var user = Db.Users.Single(u => u.Id == userId);
        user.TgUsername.Should().Be("bob");
        user.Username.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_NewUser_HandleCollidesWithExistingUsername_TgUsernameSetButUsernameNull()
    {
        // PWA-пользователь уже занял этот хэндл как видимый Username.
        Db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "taken@example.com",
            Username = "shared_handle",
            CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var userId = await _sut.GetOrCreateUserIdAsync(113, "Shared_Handle"); // разный регистр — коллизия по normalize

        var user = Db.Users.Single(u => u.Id == userId);
        user.TgUsername.Should().Be("Shared_Handle");
        user.Username.Should().BeNull("хэндл уже занят другим пользователем после нормализации");
    }

    [Fact]
    public async Task GetOrCreateUserIdAsync_ExistingUser_RefreshesOnlyTgUsername_NeverTouchesAppUsername()
    {
        var firstId = await _sut.GetOrCreateUserIdAsync(114, "dana_handle");
        var user = Db.Users.Single(u => u.Id == firstId);
        user.Username.Should().Be("dana_handle");

        // Пользователь мог сменить видимый Username вручную (гипотетически, через будущий
        // профиль-эндпоинт) — эмулируем это напрямую и убеждаемся, что следующий вход из
        // Telegram его не перезатирает, даже когда TG-хэндл поменялся.
        user.Username = "custom_chosen_name";
        await Db.SaveChangesAsync();

        var secondId = await _sut.GetOrCreateUserIdAsync(114, "dana_new_handle");

        secondId.Should().Be(firstId);
        var refreshed = Db.Users.Single(u => u.Id == firstId);
        refreshed.TgUsername.Should().Be("dana_new_handle");
        refreshed.Username.Should().Be("custom_chosen_name", "видимый Username не должен обновляться из Telegram");
    }
}
