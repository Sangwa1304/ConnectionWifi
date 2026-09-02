using System.Collections.Concurrent;
using WindowsWifiLibrary.Core.Models;
using WindowsWifiLibrary.Core.Services;

namespace TansfertApiCore;

public interface ITransferApiService
{
    Task<TransferSessionMetadata> RegisterOutgoingRequestAsync(TransferSessionMetadata metadata, CancellationToken cancellationToken);
    Task<TransferSessionMetadata?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<TransferDecision> AcceptTransferAsync(string sessionId, string? destinationFolder, CancellationToken cancellationToken);
    Task<TransferFileDownload?> OpenDownloadAsync(string? sessionId, string? relativePath, CancellationToken cancellationToken);
    Task<bool> ContainsSessionAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed record TransferFileDownload(Stream Content, long Length, string FileName);

public sealed class TransferApiService : ITransferApiService
{
    private readonly FileTransferService _fileTransferService = new();
    private readonly ConcurrentDictionary<string, TransferSessionMetadata> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITransferShareService _shareService;

    public TransferApiService(ITransferShareService? shareService = null)
    {
        _shareService = shareService ?? new TransferShareService();
    }

    public Task<TransferSessionMetadata> RegisterOutgoingRequestAsync(TransferSessionMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (string.IsNullOrWhiteSpace(metadata.SessionId))
        {
            metadata.SessionId = Guid.NewGuid().ToString();
        }

        metadata.SessionState = TransferSessionState.Negotiating;
        metadata.NegotiationStatus = TransferNegotiationStatus.Pending;

        _sessions[metadata.SessionId] = metadata;
        _shareService.RegisterSession(metadata);

        return Task.FromResult(metadata);
    }

    public Task<TransferSessionMetadata?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public async Task<TransferDecision> AcceptTransferAsync(string sessionId, string? destinationFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"La session '{sessionId}' est introuvable.");
        }

        var decision = await _fileTransferService.AcceptTransferAsync(session, destinationFolder, cancellationToken);
        session.SessionState = TransferSessionState.ReadyToSend;
        session.NegotiationStatus = TransferNegotiationStatus.Accepted;
        session.TargetFolder = decision.DestinationFolder;
        _sessions[sessionId] = session;
        return decision;
    }

    public Task<TransferFileDownload?> OpenDownloadAsync(string? sessionId, string? relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Task.FromResult<TransferFileDownload?>(null);
        }

        var normalizedPath = NormalizeRelativePath(relativePath);

        if (!string.IsNullOrWhiteSpace(sessionId) && _shareService.TryGetFilePath(sessionId, normalizedPath, out var fullPath))
        {
            return Task.FromResult<TransferFileDownload?>(BuildDownload(fullPath));
        }

        if (_shareService.TryGetAnyFilePath(normalizedPath, out var resolvedSessionId, out var resolvedFullPath))
        {
            return Task.FromResult<TransferFileDownload?>(BuildDownload(resolvedFullPath));
        }

        return Task.FromResult<TransferFileDownload?>(null);
    }

    public Task<bool> ContainsSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!string.IsNullOrWhiteSpace(sessionId) && _sessions.ContainsKey(sessionId));
    }

    private static TransferFileDownload? BuildDownload(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        var stream = File.OpenRead(fullPath);
        return new TransferFileDownload(stream, stream.Length, Path.GetFileName(fullPath));
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        return (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');
    }
}
