using System.Security.Cryptography;
using System.Text;

namespace MtVid;

internal static class CryptoHelpers
{
    private static readonly byte[] PasswordCheckText = Encoding.UTF8.GetBytes("mtvid-password-check-v1");

    internal sealed class KeyMaterial
    {
        public required byte[] EncryptionKey { get; init; }
        public required byte[] VerifierKey { get; init; }
    }

    public static KeyMaterial DeriveKeys(string password, byte[] salt, int iterations)
    {
        using Rfc2898DeriveBytes kdf = new(password, salt, iterations, HashAlgorithmName.SHA256);
        byte[] derived = kdf.GetBytes(64);

        byte[] encryptionKey = new byte[32];
        byte[] verifierKey = new byte[32];
        Buffer.BlockCopy(derived, 0, encryptionKey, 0, 32);
        Buffer.BlockCopy(derived, 32, verifierKey, 0, 32);
        CryptographicOperations.ZeroMemory(derived);

        return new KeyMaterial
        {
            EncryptionKey = encryptionKey,
            VerifierKey = verifierKey
        };
    }

    public static byte[] BuildPasswordVerifier(byte[] verifierKey)
    {
        using HMACSHA256 hmac = new(verifierKey);
        byte[] full = hmac.ComputeHash(PasswordCheckText);
        byte[] truncated = new byte[PackageHeader.PasswordVerifierSizeBytes];
        Buffer.BlockCopy(full, 0, truncated, 0, truncated.Length);
        CryptographicOperations.ZeroMemory(full);
        return truncated;
    }

    public static bool IsPasswordValid(string password, PackageHeader header, out byte[] encryptionKey)
    {
        KeyMaterial keys = DeriveKeys(password, header.Salt, header.KdfIterations);
        try
        {
            byte[] expectedVerifier = BuildPasswordVerifier(keys.VerifierKey);
            bool isValid = CryptographicOperations.FixedTimeEquals(expectedVerifier, header.PasswordVerifier);
            CryptographicOperations.ZeroMemory(expectedVerifier);
            if (!isValid)
            {
                CryptographicOperations.ZeroMemory(keys.EncryptionKey);
                encryptionKey = Array.Empty<byte>();
                return false;
            }

            encryptionKey = keys.EncryptionKey;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys.VerifierKey);
        }
    }
}
