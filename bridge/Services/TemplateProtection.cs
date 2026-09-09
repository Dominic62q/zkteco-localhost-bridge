using System.Security.Cryptography;

namespace Bridge.Services;

/// <summary>
/// DPAPI (CurrentUser scope) protection for fingerprint templates.
/// Same Windows user decrypts across bridge restarts; nothing leaves the PC.
/// </summary>
public static class TemplateProtection
{
    public const string Algorithm = "zkfp2+dpapi";

    public static byte[] Protect(byte[] plain, int count)
    {
        var exact = new byte[count];
        Buffer.BlockCopy(plain, 0, exact, 0, count);
        try
        {
            return ProtectedData.Protect(exact, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exact);
        }
    }

    public static byte[] Unprotect(byte[] cipher) =>
        ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
}
