using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Api.Security;

/// <summary>
/// Constant-time сравнение логин/пароль — общий примитив для двух независимых поверхностей
/// входа: <see cref="AdminBasicAuth"/> (Hangfire/Swagger, DevTools:AdminUser/Password) и
/// AdminSessionEndpoints (панель ADR-0009, Admin:User/Password). Вынесен сюда, а не продублирован,
/// именно потому что это security-примитив — timing-safe сравнение легко случайно сломать
/// копипастой (обычный "==" на строке секрета создаёт таймингового оракула).
/// </summary>
public static class CredentialComparer
{
    public static bool Matches(string user, string password, string? expectedUser, string? expectedPassword)
    {
        var userMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(expectedUser ?? string.Empty));
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expectedPassword ?? string.Empty));
        return userMatches && passwordMatches;
    }
}
