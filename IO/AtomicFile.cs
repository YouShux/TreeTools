using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TreeTools.IO;

public static class AtomicFile
{
    public static void WriteAllText
    (
        string   path,
        string   content,
        Encoding encoding
    ) =>
        Write(path, stream => stream.Write(encoding.GetBytes(content)));

    public static Task WriteAllTextAsync
    (
        string            path,
        string            content,
        Encoding          encoding,
        CancellationToken cancellationToken = default
    ) =>
        WriteAsync(path, async stream =>
        {
            var bytes = encoding.GetBytes(content);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public static void WriteAllBytes
    (
        string path,
        byte[] content
    ) =>
        Write(path, stream => stream.Write(content));

    private static void Write
    (
        string         path,
        Action<Stream> write
    )
    {
        var tempPath = CreateTempPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }

            File.Move(tempPath, path, true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task WriteAsync
    (
        string             path,
        Func<Stream, Task> write,
        CancellationToken  cancellationToken
    )
    {
        var tempPath = CreateTempPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             useAsync: true))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string CreateTempPath(string path) =>
        $"{path}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
