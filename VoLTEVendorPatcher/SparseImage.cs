using System.Buffers.Binary;

namespace VoLTEVendorPatcher;

internal readonly record struct SparseHeader(uint BlockSize, uint TotalBlocks, uint TotalChunks, ushort FileHeaderSize, ushort ChunkHeaderSize)
{
    public long RawLength => checked((long)BlockSize * TotalBlocks);
}

internal static class SparseImage
{
    private const uint Magic = 0xED26FF3A;
    private const ushort MajorVersion = 1;
    private const ushort RawChunk = 0xCAC1;
    private const ushort FillChunk = 0xCAC2;
    private const ushort DontCareChunk = 0xCAC3;
    private const ushort Crc32Chunk = 0xCAC4;

    public static async Task<(ImageFormat Format, SparseHeader? SparseHeader)> DetectAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var first = new byte[4];
        if (await stream.ReadAsync(first, token) < 4) throw new PatcherException("Image quá nhỏ hoặc không thể đọc.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(first) != Magic) return (ImageFormat.RawExt4, null);
        stream.Seek(0, SeekOrigin.Begin);
        var header = await ReadHeaderAsync(stream, token);
        return (ImageFormat.Sparse, header);
    }

    public static async Task ToRawAsync(string input, string output, SparseHeader header, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        if (header.BlockSize == 0 || header.FileHeaderSize < 28 || header.ChunkHeaderSize < 12) throw new PatcherException("Sparse header không hợp lệ.");
        await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        _ = await ReadHeaderAsync(source, token);
        if (header.FileHeaderSize > 28) source.Seek(header.FileHeaderSize - 28, SeekOrigin.Current);
        long blocks = 0;
        var zero = new byte[Math.Min((int)header.BlockSize * 128, 1024 * 1024)];
        for (uint index = 0; index < header.TotalChunks; index++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = await ReadChunkHeaderAsync(source, header.ChunkHeaderSize, token);
            var bytes = checked((long)chunk.ChunkBlocks * header.BlockSize);
            switch (chunk.Type)
            {
                case RawChunk:
                    if (chunk.TotalSize != checked((ulong)header.ChunkHeaderSize + (ulong)bytes)) throw new PatcherException("Sparse RAW chunk có kích thước sai.");
                    await CopyBytesAsync(source, destination, bytes, token);
                    break;
                case FillChunk:
                    if (chunk.TotalSize != checked((ulong)header.ChunkHeaderSize + 4UL)) throw new PatcherException("Sparse FILL chunk có kích thước sai.");
                    var pattern = new byte[4]; await source.ReadExactlyAsync(pattern, token);
                    for (long remaining = bytes; remaining > 0; remaining -= zero.Length)
                    {
                        var count = (int)Math.Min(remaining, zero.Length);
                        for (var p = 0; p < count; p++) zero[p] = pattern[p % 4];
                        await destination.WriteAsync(zero.AsMemory(0, count), token);
                    }
                    break;
                case DontCareChunk:
                    if (chunk.TotalSize != header.ChunkHeaderSize) throw new PatcherException("Sparse DONT_CARE chunk có kích thước sai.");
                    for (long remaining = bytes; remaining > 0; remaining -= zero.Length) await destination.WriteAsync(zero.AsMemory(0, (int)Math.Min(remaining, zero.Length)), token);
                    break;
                case Crc32Chunk:
                    if (chunk.TotalSize != header.ChunkHeaderSize + 4 || chunk.ChunkBlocks != 0) throw new PatcherException("Sparse CRC chunk không hợp lệ.");
                    var crc = new byte[4]; await source.ReadExactlyAsync(crc, token);
                    break;
                default: throw new PatcherException($"Sparse chunk type 0x{chunk.Type:X4} chưa được hỗ trợ.");
            }
            blocks += chunk.ChunkBlocks;
            progress?.Report(new(12 + (int)(28 * (index + 1) / Math.Max(1, header.TotalChunks)), "Giải sparse image…"));
        }
        if (blocks != header.TotalBlocks || destination.Length != header.RawLength) throw new PatcherException("Sparse image không khớp kích thước header.");
        await destination.FlushAsync(token);
    }

    public static async Task ToSparseAsync(string raw, string output, uint blockSize, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        if (blockSize == 0 || new FileInfo(raw).Length % blockSize != 0) throw new PatcherException("Raw image không chia hết cho block size.");
        var chunks = new List<(bool Zero, uint Blocks, long Offset)>();
        await using (var source = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            var block = new byte[checked((int)blockSize)]; long offset = 0; bool? currentZero = null; uint count = 0; long start = 0;
            while (source.Position < source.Length)
            {
                await source.ReadExactlyAsync(block, token);
                var isZero = IsZero(block);
                if (currentZero is null) { currentZero = isZero; start = offset; }
                if (currentZero != isZero) { chunks.Add((currentZero.Value, count, start)); currentZero = isZero; count = 0; start = offset; }
                count++; offset += blockSize;
            }
            if (count > 0 && currentZero.HasValue) chunks.Add((currentZero.Value, count, start));
        }
        await using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var header = new byte[28]; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), MajorVersion); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), 0); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), 28); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), 12); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), blockSize); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), checked((uint)(new FileInfo(raw).Length / blockSize))); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), checked((uint)chunks.Count)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 0); await outputStream.WriteAsync(header, token);
        await using var sourceStream = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var copyBuffer = new byte[1024 * 1024];
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index]; var chunkHeader = new byte[12]; BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(0, 2), chunk.Zero ? DontCareChunk : RawChunk); BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(2, 2), 0); BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(4, 4), chunk.Blocks); BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(8, 4), chunk.Zero ? 12U : checked(12U + chunk.Blocks * blockSize)); await outputStream.WriteAsync(chunkHeader, token);
            if (!chunk.Zero)
            {
                sourceStream.Seek(chunk.Offset, SeekOrigin.Begin); long remaining = (long)chunk.Blocks * blockSize;
                while (remaining > 0) { var read = await sourceStream.ReadAsync(copyBuffer.AsMemory(0, (int)Math.Min(remaining, copyBuffer.Length)), token); if (read == 0) throw new PatcherException("Raw image bị ngắn trong lúc tạo sparse."); await outputStream.WriteAsync(copyBuffer.AsMemory(0, read), token); remaining -= read; }
            }
            progress?.Report(new(78 + (int)(15 * (index + 1) / Math.Max(1, chunks.Count)), "Đóng gói sparse image…"));
        }
        await outputStream.FlushAsync(token);
    }

    private static async Task<SparseHeader> ReadHeaderAsync(FileStream stream, CancellationToken token)
    {
        var data = new byte[28]; await stream.ReadExactlyAsync(data, token);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)); var major = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2)); var fileHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)); var chunkHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10, 2));
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
        var totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4));
        var totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
        if (magic != Magic || major != MajorVersion || fileHeader < 28 || chunkHeader < 12 || blockSize == 0 || totalBlocks == 0)
            throw new PatcherException("Android sparse header khÃ´ng há»£p lá»‡.");
        if (magic != Magic || major != MajorVersion) throw new PatcherException("Android sparse header không hợp lệ.");
        return new SparseHeader(blockSize, totalBlocks, totalChunks, fileHeader, chunkHeader);
    }

    private static async Task<(ushort Type, uint ChunkBlocks, uint TotalSize)> ReadChunkHeaderAsync(FileStream stream, ushort headerSize, CancellationToken token)
    {
        var data = new byte[12]; await stream.ReadExactlyAsync(data, token); var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (headerSize > 12) stream.Seek(headerSize - 12, SeekOrigin.Current);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)), totalSize);
    }

    private static async Task CopyBytesAsync(Stream source, Stream destination, long bytes, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024]; while (bytes > 0) { var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bytes, buffer.Length)), token); if (read == 0) throw new PatcherException("Sparse RAW chunk bị ngắn."); await destination.WriteAsync(buffer.AsMemory(0, read), token); bytes -= read; }
    }
    private static bool IsZero(byte[] block) { foreach (var b in block) if (b != 0) return false; return true; }
}
