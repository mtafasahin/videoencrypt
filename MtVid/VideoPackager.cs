using System.Security.Cryptography;

namespace MtVid;

internal static class VideoPackager
{
    public static void EncryptVideo(
        string inputVideoPath,
        string outputPackagePath,
        string password,
        int chunkSizeBytes,
        string contentType,
        int iterations,
        Action<long, long>? onProgress = null,
        string? originalFileName = null)
    {
        if (!File.Exists(inputVideoPath))
        {
            throw new FileNotFoundException("Input video not found.", inputVideoPath);
        }

        byte[] salt = RandomNumberGenerator.GetBytes(PackageHeader.SaltSizeBytes);
        byte[] noncePrefix = RandomNumberGenerator.GetBytes(PackageHeader.NoncePrefixSizeBytes);

        CryptoHelpers.KeyMaterial keys = CryptoHelpers.DeriveKeys(password, salt, iterations);
        byte[] passwordVerifier = CryptoHelpers.BuildPasswordVerifier(keys.VerifierKey);

        try
        {
            using FileStream input = new(inputVideoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long inputLength = input.Length;
            int chunkCount = inputLength == 0 ? 0 : checked((int)((inputLength + chunkSizeBytes - 1) / chunkSizeBytes));

            PackageHeader header = new()
            {
                ChunkSize = chunkSizeBytes,
                OriginalLength = inputLength,
                ChunkCount = chunkCount,
                KdfIterations = iterations,
                Salt = salt,
                NoncePrefix = noncePrefix,
                PasswordVerifier = passwordVerifier,
                ContentType = contentType,
                OriginalFileName = string.IsNullOrWhiteSpace(originalFileName)
                    ? Path.GetFileName(inputVideoPath)
                    : Path.GetFileName(originalFileName)
            };

            using FileStream output = new(outputPackagePath, FileMode.Create, FileAccess.Write, FileShare.None);
            header.WriteTo(output);

            byte[] plaintext = new byte[chunkSizeBytes];
            byte[] ciphertext = new byte[chunkSizeBytes];
            byte[] tag = new byte[PackageHeader.TagSizeBytes];

            using AesGcm aesGcm = new(keys.EncryptionKey, PackageHeader.TagSizeBytes);
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                int bytesRead = ReadExactUpTo(input, plaintext, chunkSizeBytes);
                byte[] nonce = PackageHeader.BuildNonce(header.NoncePrefix, chunkIndex);
                aesGcm.Encrypt(nonce, plaintext.AsSpan(0, bytesRead), ciphertext.AsSpan(0, bytesRead), tag);

                output.Write(ciphertext, 0, bytesRead);
                output.Write(tag, 0, tag.Length);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
                onProgress?.Invoke(input.Position, inputLength);
            }

            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys.EncryptionKey);
            CryptographicOperations.ZeroMemory(keys.VerifierKey);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(noncePrefix);
            CryptographicOperations.ZeroMemory(passwordVerifier);
        }
    }

    private static int ReadExactUpTo(Stream source, byte[] buffer, int maxBytes)
    {
        int totalRead = 0;
        while (totalRead < maxBytes)
        {
            int read = source.Read(buffer, totalRead, maxBytes - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
