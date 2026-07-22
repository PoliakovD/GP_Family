namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// Шифрование строковых полей БД (навешивается AppDbContext'ом на [Encrypted]-свойства).
/// Формат хранения: "enc:{keyId}:{base64(nonce||tag||ciphertext)}".
/// </summary>
public interface IFieldCipher
{
    string Protect(string plaintext);

    string Unprotect(string stored);
}
