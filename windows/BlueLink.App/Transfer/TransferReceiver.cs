using System.Security.Cryptography;

namespace BlueLink.Transfer;

public sealed class TransferReceiver : IAsyncDisposable
{
    private static readonly TimeSpan[] CommitRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500)
    ];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    private readonly string _target;
    private readonly string _partial;
    private readonly string _metadata;
    private readonly long _size;
    private readonly int _extentSize;
    private readonly byte[] _wholeHash;
    private readonly bool[] _completed;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _finishGate = new(1, 1);
    private bool _writerClosed;
    private bool _committed;

    public TransferReceiver(string managedRoot, FileOffer offer)
    {
        _target = ResolveSafe(managedRoot, offer.Name);
        _partial = $"{_target}.{offer.Id:N}.part";
        _metadata = $"{_partial}.resume";
        _size = offer.Size;
        _extentSize = offer.ExtentSize;
        _wholeHash = offer.Hash;
        _completed = new bool[checked((int)((offer.Size + offer.ExtentSize - 1) / offer.ExtentSize))];
        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        var resume = TryLoadResume();
        _stream = new FileStream(_partial, resume ? FileMode.Open : FileMode.Create,
            FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (_stream.Length > _size) _stream.SetLength(_size);
    }

    public long ContiguousBytes
    {
        get { var count = 0; while (count < _completed.Length && _completed[count]) count++; return Math.Min(_size, (long)count * _extentSize); }
    }

    public async Task AcceptAsync(FileExtent extent, CancellationToken token)
    {
        if (extent.Index >= _completed.Length) throw new InvalidDataException("Extent index is out of range");
        if (_completed[extent.Index]) return;
        var offset = (long)extent.Index * _extentSize;
        var expected = (int)Math.Min(_extentSize, _size - offset);
        if (extent.Data.Length != expected)
            throw new InvalidDataException(
                $"Extent length mismatch: index={extent.Index}, expected={expected}, actual={extent.Data.Length}");
        var actualHash = SHA256.HashData(extent.Data);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, extent.Hash))
            throw new InvalidDataException(
                $"Extent hash mismatch: index={extent.Index}, expected={Convert.ToHexString(extent.Hash.AsSpan(0, 6))}, actual={Convert.ToHexString(actualHash.AsSpan(0, 6))}");
        _stream.Position = offset;
        await _stream.WriteAsync(extent.Data, token);
        await _stream.FlushAsync(token);
        _completed[extent.Index] = true;
        await PersistResumeAsync(token);
    }

    public async Task<string> FinishAsync(CancellationToken token, Action? onCommitting = null)
    {
        await _finishGate.WaitAsync(token);
        try
        {
            if (_committed) return _target;
            if (_completed.Any(value => !value)) throw new InvalidDataException("Transfer has missing extents");
            if (!_writerClosed)
            {
                await _stream.FlushAsync(token);
                if (_stream.Length != _size)
                    throw new InvalidDataException($"Whole-file length mismatch: expected={_size}, actual={_stream.Length}");
                await CloseWriterAsync();
            }

            byte[] actual;
            await using (var input = new FileStream(_partial, FileMode.Open, FileAccess.Read,
                             FileShare.Read | FileShare.Delete, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actual = await SHA256.HashDataAsync(input, token);
            }
            if (!CryptographicOperations.FixedTimeEquals(actual, _wholeHash))
            {
                DeleteIfExists(_metadata);
                DeleteIfExists(_partial);
                throw new InvalidDataException(
                    $"Whole-file hash mismatch: expected={Convert.ToHexString(_wholeHash.AsSpan(0, 6))}, actual={Convert.ToHexString(actual.AsSpan(0, 6))}");
            }

            onCommitting?.Invoke();
            await CommitWithRetryAsync(token);
            DeleteIfExists(_metadata);
            _committed = true;
            return _target;
        }
        finally
        {
            _finishGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _finishGate.WaitAsync();
        try { await CloseWriterAsync(); }
        finally { _finishGate.Release(); }
    }

    private async ValueTask CloseWriterAsync()
    {
        if (_writerClosed) return;
        await _stream.DisposeAsync();
        _writerClosed = true;
    }

    private async Task CommitWithRetryAsync(CancellationToken token)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < CommitRetryDelays.Length; attempt++)
        {
            var delay = CommitRetryDelays[attempt];
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            try
            {
                File.Move(_partial, _target, true);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                lastFailure = failure;
            }
        }
        throw new IOException(
            $"Final file commit failed after {CommitRetryDelays.Length} attempts: {lastFailure?.Message}", lastFailure);
    }

    private bool TryLoadResume()
    {
        if (!File.Exists(_partial) || !File.Exists(_metadata))
        {
            DeleteIfExists(_partial);
            DeleteIfExists(_metadata);
            return false;
        }

        try
        {
            using var input = new BinaryReader(File.Open(_metadata, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (input.ReadUInt32() != 0x31525442 || input.ReadInt64() != _size || input.ReadInt32() != _extentSize)
                throw new InvalidDataException("Resume metadata does not match the offer");
            var hashLength = input.ReadInt32();
            var hash = input.ReadBytes(hashLength);
            if (hashLength != _wholeHash.Length || !CryptographicOperations.FixedTimeEquals(hash, _wholeHash))
                throw new InvalidDataException("Resume hash does not match the offer");
            var bitmapLength = input.ReadInt32();
            var bitmap = input.ReadBytes(bitmapLength);
            if (bitmap.Length != bitmapLength || input.BaseStream.Position != input.BaseStream.Length)
                throw new InvalidDataException("Resume metadata is truncated");
            for (var index = 0; index < _completed.Length; index++)
                _completed[index] = index / 8 < bitmap.Length && (bitmap[index / 8] & (1 << (index % 8))) != 0;
            if (bitmapLength > (_completed.Length + 7) / 8)
                throw new InvalidDataException("Resume bitmap is out of range");
            var lastCompleted = Array.FindLastIndex(_completed, value => value);
            var requiredLength = lastCompleted < 0 ? 0 : Math.Min(_size, (long)(lastCompleted + 1) * _extentSize);
            if (new FileInfo(_partial).Length < requiredLength)
                throw new InvalidDataException("Partial file is shorter than its resume bitmap");
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Array.Clear(_completed);
            DeleteIfExists(_partial);
            DeleteIfExists(_metadata);
            return false;
        }
    }

    private async Task PersistResumeAsync(CancellationToken token)
    {
        var temporary = _metadata + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            writer.Write(0x31525442u); // BTR1
            writer.Write(_size);
            writer.Write(_extentSize);
            writer.Write(_wholeHash.Length);
            writer.Write(_wholeHash);
            var bitmap = new byte[(_completed.Length + 7) / 8];
            for (var index = 0; index < _completed.Length; index++)
                if (_completed[index]) bitmap[index / 8] |= (byte)(1 << (index % 8));
            writer.Write(bitmap.Length);
            writer.Write(bitmap);
            writer.Flush();
            await output.FlushAsync(token);
        }
        File.Move(temporary, _metadata, true);
    }

    private static void DeleteIfExists(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string ResolveSafe(string rootValue, string name)
    {
        var normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains('/')
            || Path.IsPathRooted(normalized) || normalized.EndsWith('.') || normalized.EndsWith(' '))
            throw new InvalidDataException("Unsafe received filename");
        var stem = normalized.Split('.')[0];
        if (Reserved.Contains(stem)) throw new InvalidDataException("Reserved received filename");
        var root = Path.GetFullPath(rootValue) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Received path escapes managed directory");
        return target;
    }
}
