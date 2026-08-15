using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WFly.Services;

internal static class JsonStore
{
    private static readonly IJsonTypeInfoResolver TypeInfoResolver = new DefaultJsonTypeInfoResolver();

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = TypeInfoResolver,
    };

    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = TypeInfoResolver,
    };

    public static async Task<T> ReadOrDefaultAsync<T>(string path, Func<T> defaultFactory, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return defaultFactory();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
        return value ?? throw new InvalidDataException($"状态文件无效：{path}");
    }

    public static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("无法确定状态文件目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
