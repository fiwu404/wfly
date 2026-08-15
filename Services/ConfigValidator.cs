using System.Text.Json;

namespace WFly.Services;

internal static class ConfigValidator
{
    private const long MaximumConfigBytes = 10 * 1024 * 1024;

    public static async Task ValidateAsync(string configPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到所选配置文件。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配置文件必须是 .json 文件。");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidDataException("配置文件为空。");
        }

        if (fileInfo.Length > MaximumConfigBytes)
        {
            throw new InvalidDataException($"配置文件超过 {MaximumConfigBytes / 1024 / 1024} MB 限制。");
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 128,
        }, cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("配置文件的 JSON 根节点必须是对象。");
        }
    }
}
