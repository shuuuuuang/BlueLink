namespace BlueLink.Transfer;

public static class OutgoingSnapshot
{
    public static async Task<string> CreateAsync(string sourcePath, string cacheRoot, Guid transferId,
        CancellationToken token)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("待发送文件不存在", source);
        Directory.CreateDirectory(cacheRoot);
        var target = Path.Combine(cacheRoot, $"{transferId:N}.snapshot");
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var expected = input.Length;
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 128 * 1024, token);
                await output.FlushAsync(token);
                if (output.Length != expected)
                    throw new IOException($"文件快照长度不一致：expected={expected}, actual={output.Length}");
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }
}
