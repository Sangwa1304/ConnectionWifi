using System.Collections.Concurrent;
using WindowsWifiLibrary.Core.Models;

namespace TansfertApiCore;

public interface ITransferShareService
{
    void RegisterSession(TransferSessionMetadata metadata);
    bool TryGetFilePath(string sessionId, string relativePath, out string fullPath);
    bool TryGetAnyFilePath(string relativePath, out string sessionId, out string fullPath);
}

public sealed class TransferShareService : ITransferShareService
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sessionFiles = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterSession(TransferSessionMetadata metadata)
    {
        var files = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in FlattenItems(metadata.Items))
        {
            if (item.IsDirectory || string.IsNullOrWhiteSpace(item.FullLocalPath) || !File.Exists(item.FullLocalPath))
            {
                continue;
            }

            files[NormalizeRelativePath(item.RelativePath)] = item.FullLocalPath;
        }

        _sessionFiles[metadata.SessionId] = files;
    }

    public bool TryGetFilePath(string sessionId, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (!_sessionFiles.TryGetValue(sessionId, out var files))
        {
            return false;
        }

        var normalizedPath = NormalizeRelativePath(relativePath);
        return files.TryGetValue(normalizedPath, out fullPath) && File.Exists(fullPath);
    }

    public bool TryGetAnyFilePath(string relativePath, out string sessionId, out string fullPath)
    {
        sessionId = string.Empty;
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        foreach (var pair in _sessionFiles)
        {
            if (pair.Value.TryGetValue(NormalizeRelativePath(relativePath), out var candidate) && File.Exists(candidate))
            {
                sessionId = pair.Key;
                fullPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TransferItemMetadata> FlattenItems(IEnumerable<TransferItemMetadata> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in FlattenItems(item.Children))
            {
                yield return child;
            }
        }
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        return (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');
    }
}
