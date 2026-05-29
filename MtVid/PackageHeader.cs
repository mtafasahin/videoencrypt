using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MtVid;

internal sealed class PackageHeader
{
    private static readonly byte[] Magic = "MTAF"u8.ToArray();

    public const byte CurrentVersion = 2;
    public const int SaltSizeBytes = 16;
    public const int NoncePrefixSizeBytes = 4;
    public const int PasswordVerifierSizeBytes = 16;
    public const int TagSizeBytes = 16;

    public byte Version { get; init; } = CurrentVersion;
    public int ChunkSize { get; init; }
    public long OriginalLength { get; init; }
    public int ChunkCount { get; init; }
    public int KdfIterations { get; init; }
    public byte[] Salt { get; init; } = Array.Empty<byte>();
    public byte[] NoncePrefix { get; init; } = Array.Empty<byte>();
    public byte[] PasswordVerifier { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
    public string? OriginalFileName { get; init; }
    public long HeaderSize { get; init; }

    public void WriteTo(Stream stream)
    {
        if (Salt.Length != SaltSizeBytes)
        {
            throw new InvalidDataException($"Salt must be {SaltSizeBytes} bytes.");
        }

        if (NoncePrefix.Length != NoncePrefixSizeBytes)
        {
            throw new InvalidDataException($"Nonce prefix must be {NoncePrefixSizeBytes} bytes.");
        }

        if (PasswordVerifier.Length != PasswordVerifierSizeBytes)
        {
            throw new InvalidDataException($"Password verifier must be {PasswordVerifierSizeBytes} bytes.");
        }

        byte[] contentTypeBytes = Encoding.UTF8.GetBytes(ContentType);
        if (contentTypeBytes.Length > byte.MaxValue)
        {
            throw new InvalidDataException("Content type is too long.");
        }

        string originalName = OriginalFileName ?? string.Empty;
        byte[] originalNameBytes = Encoding.UTF8.GetBytes(originalName);
        if (originalNameBytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("Original file name is too long.");
        }

        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(ChunkSize);
        writer.Write(OriginalLength);
        writer.Write(ChunkCount);
        writer.Write(KdfIterations);
        writer.Write(Salt);
        writer.Write(NoncePrefix);
        writer.Write(PasswordVerifier);
        writer.Write((byte)contentTypeBytes.Length);
        writer.Write(contentTypeBytes);
        writer.Write((ushort)originalNameBytes.Length);
        writer.Write(originalNameBytes);
    }

    public static PackageHeader ReadFrom(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid package magic. This is not a valid .mtaf file.");
        }

        byte version = reader.ReadByte();
        if (version is not 1 and not CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported package version: {version}");
        }

        int chunkSize = reader.ReadInt32();
        long originalLength = reader.ReadInt64();
        int chunkCount = reader.ReadInt32();
        int kdfIterations = reader.ReadInt32();
        byte[] salt = reader.ReadBytes(SaltSizeBytes);
        byte[] noncePrefix = reader.ReadBytes(NoncePrefixSizeBytes);
        byte[] verifier = reader.ReadBytes(PasswordVerifierSizeBytes);
        int contentTypeLength = reader.ReadByte();
        byte[] contentTypeBytes = reader.ReadBytes(contentTypeLength);
        string? originalFileName = null;

        if (version >= 2)
        {
            ushort originalNameLength = reader.ReadUInt16();
            byte[] originalNameBytes = reader.ReadBytes(originalNameLength);
            if (originalNameBytes.Length != originalNameLength)
            {
                throw new InvalidDataException("Package header is truncated or corrupted.");
            }

            originalFileName = Encoding.UTF8.GetString(originalNameBytes);
            if (string.IsNullOrWhiteSpace(originalFileName))
            {
                originalFileName = null;
            }
        }

        if (salt.Length != SaltSizeBytes || noncePrefix.Length != NoncePrefixSizeBytes || verifier.Length != PasswordVerifierSizeBytes || contentTypeBytes.Length != contentTypeLength)
        {
            throw new InvalidDataException("Package header is truncated or corrupted.");
        }

        if (chunkSize <= 0 || originalLength < 0 || chunkCount < 0 || kdfIterations < 10000)
        {
            throw new InvalidDataException("Package header has invalid values.");
        }

        return new PackageHeader
        {
            Version = version,
            ChunkSize = chunkSize,
            OriginalLength = originalLength,
            ChunkCount = chunkCount,
            KdfIterations = kdfIterations,
            Salt = salt,
            NoncePrefix = noncePrefix,
            PasswordVerifier = verifier,
            ContentType = Encoding.UTF8.GetString(contentTypeBytes),
            OriginalFileName = originalFileName,
            HeaderSize = stream.Position
        };
    }

    public static byte[] BuildNonce(byte[] noncePrefix, long chunkIndex)
    {
        if (noncePrefix.Length != NoncePrefixSizeBytes)
        {
            throw new ArgumentException($"Nonce prefix must be {NoncePrefixSizeBytes} bytes.", nameof(noncePrefix));
        }

        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        Buffer.BlockCopy(noncePrefix, 0, nonce, 0, noncePrefix.Length);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(NoncePrefixSizeBytes), chunkIndex);
        return nonce;
    }
}
