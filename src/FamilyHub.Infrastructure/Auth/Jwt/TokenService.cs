using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FamilyHub.Infrastructure.Auth.Jwt;

public class TokenService(AppDbContext db, IOptions<JwtOptions> options) : ITokenService
{
    public async Task<IssuedSession> IssueAsync(
        Guid userId, string email, string? createdByIp, string? deviceInfo, CancellationToken ct = default)
    {
        var (refreshToken, refreshExpiresAt, sessionId) =
            await CreateSessionAsync(userId, createdByIp, deviceInfo, ct);
        var (accessToken, accessExpiresAt) = CreateAccessToken(userId, email, sessionId);
        return new IssuedSession(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt);
    }

    public async Task<IssuedSession?> RefreshAsync(
        string rawRefreshToken, string? createdByIp, string? deviceInfo, CancellationToken ct = default)
    {
        var hash = TokenHasher.Hash(rawRefreshToken);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is null) return null;

        var now = DateTime.UtcNow;

        if (session.RevokedAt is not null)
        {
            // Сессия уже была заменена более новой при прошлой ротации — предъявление старого
            // refresh-токена после этого возможно только при краже (клиент никогда не должен
            // реиспользовать отданный ему refresh после ротации). Реакция — убить всю цепочку.
            if (session.ReplacedByTokenId is not null)
                await RevokeAllForUserAsync(session.UserId, ct);
            return null;
        }

        if (session.ExpiresAt <= now) return null;

        var email = await db.Users.Where(u => u.Id == session.UserId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        if (email is null) return null; // аккаунт удалён/аномалия — рефреш недействителен

        // Выпуск новой сессии и отзыв старой — одна транзакция (аудит, находка Medium #3):
        // CreateSessionAsync коммитит собственным SaveChangesAsync, отзыв старой — отдельным
        // ниже. Без общей транзакции крах между ними оставлял бы либо две одновременно живые
        // сессии на устройство (новая выпущена, старая не отозвана), либо отозванную старую без
        // записанной новой (клиент теряет сессию совсем).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (refreshToken, refreshExpiresAt, replacementId) =
            await CreateSessionAsync(session.UserId, createdByIp, deviceInfo, ct);
        var (accessToken, accessExpiresAt) = CreateAccessToken(session.UserId, email, replacementId);

        session.RevokedAt = now;
        session.ReplacedByTokenId = replacementId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new IssuedSession(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt);
    }

    public async Task RevokeAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var hash = TokenHasher.Hash(rawRefreshToken);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash && s.RevokedAt == null, ct);
        if (session is null) return;

        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
    }

    public async Task<bool> RevokeByIdAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var affected = await db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        return affected > 0;
    }

    private (string Token, DateTime ExpiresAt) CreateAccessToken(Guid userId, string email, Guid sessionId)
    {
        var jwt = options.Value;
        if (string.IsNullOrWhiteSpace(jwt.SigningKey))
            throw new InvalidOperationException("Jwt:SigningKey не задан — выпуск токенов невозможен.");

        var expiresAt = DateTime.UtcNow.Add(jwt.AccessTokenLifetime);
        // KeyId → заголовок `kid` токена (ADR-0009, чисто диагностический — валидация пробует
        // все ключи связки, см. IssuerSigningKeys в Program.cs, а не выбирает по kid).
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey))
        {
            KeyId = jwt.ActiveKeyId,
        };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(FamilyHubClaimTypes.UserId, userId.ToString()),
                new Claim(FamilyHubClaimTypes.Email, email),
                new Claim(FamilyHubClaimTypes.AuthProvider, "email"),
                new Claim(FamilyHubClaimTypes.SessionId, sessionId.ToString()),
            ],
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    private async Task<(string Token, DateTime ExpiresAt, Guid SessionId)> CreateSessionAsync(
        Guid userId, string? createdByIp, string? deviceInfo, CancellationToken ct)
    {
        var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(options.Value.RefreshTokenLifetime);

        var entity = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RefreshTokenHash = TokenHasher.Hash(raw),
            ExpiresAt = expiresAt,
            CreatedAt = now,
            IpAddress = createdByIp,
            DeviceInfo = deviceInfo,
        };
        db.UserSessions.Add(entity);
        await db.SaveChangesAsync(ct);

        return (raw, expiresAt, entity.Id);
    }
}
