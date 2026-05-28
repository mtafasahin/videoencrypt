using System.Security.Cryptography;

namespace MtVid;

internal sealed class PackageDecryptingStream : Stream
{
    private readonly FileStream _file;
    private readonly PackageHeader _header;
    private readonly byte[] _encryptionKey;
    private long _position;
    private long _cachedChunkIndex = -1;
    private byte[] _cachedPlaintext = Array.Empty<byte>();

    public PackageDecryptingStream(string packagePath, PackageHeader header, byte[] encryptionKey)
    {
        _file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _header = header;
        _encryptionKey = new byte[encryptionKey.Length];
        Buffer.BlockCopy(encryptionKey, 0, _encryptionKey, 0, _encryptionKey.Length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _header.OriginalLength;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _header.OriginalLength || count == 0)
        {
            return 0;
        }

        if (offset < 0 || offset >= buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        int totalCopied = 0;
        while (count > 0 && _position < _header.OriginalLength)
        {
            long chunkIndex = _position / _header.ChunkSize;
            int offsetInsideChunk = (int)(_position % _header.ChunkSize);
            LoadChunk(chunkIndex);

            int available = _cachedPlaintext.Length - offsetInsideChunk;
            int requested = Math.Min(available, count);

            Buffer.BlockCopy(_cachedPlaintext, offsetInsideChunk, buffer, offset + totalCopied, requested);
            totalCopied += requested;
            count -= requested;
            _position += requested;
        }

        return totalCopied;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _header.OriginalLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (next < 0)
        {
            throw new IOException("Cannot seek to a negative position.");
        }

        _position = next;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
            CryptographicOperations.ZeroMemory(_encryptionKey);
            if (_cachedPlaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(_cachedPlaintext);
            }
        }

        base.Dispose(disposing);
    }

    private void LoadChunk(long chunkIndex)
    {
        if (chunkIndex == _cachedChunkIndex)
        {
            return;
        }

        int plaintextLength = GetChunkPlaintextLength(chunkIndex);
        long encryptedOffset = _header.HeaderSize + chunkIndex * ((long)_header.ChunkSize + PackageHeader.TagSizeBytes);

        byte[] ciphertext = new byte[plaintextLength];
        byte[] tag = new byte[PackageHeader.TagSizeBytes];

        _file.Position = encryptedOffset;
        ReadExactly(_file, ciphertext, 0, plaintextLength);
        ReadExactly(_file, tag, 0, tag.Length);

        byte[] plaintext = new byte[plaintextLength];
        byte[] nonce = PackageHeader.BuildNonce(_header.NoncePrefix, chunkIndex);

        using AesGcm aesGcm = new(_encryptionKey, PackageHeader.TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);
        CryptographicOperations.ZeroMemory(nonce);

        if (_cachedPlaintext.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_cachedPlaintext);
        }

        _cachedPlaintext = plaintext;
        _cachedChunkIndex = chunkIndex;
    }

    private int GetChunkPlaintextLength(long chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= _header.ChunkCount)
        {
            throw new InvalidDataException("Chunk index is out of range.");
        }

        bool isLastChunk = chunkIndex == _header.ChunkCount - 1;
        if (!isLastChunk)
        {
            return _header.ChunkSize;
        }

        long consumedByFullChunks = (long)(_header.ChunkCount - 1) * _header.ChunkSize;
        return (int)(_header.OriginalLength - consumedByFullChunks);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of package while reading encrypted chunk.");
            }

            totalRead += read;
        }
     }
 }
