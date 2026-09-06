using LibraryMI.Models;
using WindowsWifiLibrary.Core.Services;
using Xunit;

namespace WindowsWifiLibrary.Core.Tests;

public class FileTransferServiceTests
{
    [Fact]
    public async Task ValidateTransferAsync_normalizes_existing_paths_and_reports_duplicates()
    {
        using var fixture = new TemporaryDirectory();
        var filePath = Path.Combine(fixture.Path, "payload.txt");
        await File.WriteAllTextAsync(filePath, "payload");

        using var service = new FileTransferService();
        var result = await service.ValidateTransferAsync(
            [filePath, $"  {filePath}  "],
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Single(result.NormalizedPaths);
        Assert.Equal(Path.GetFullPath(filePath), result.NormalizedPaths[0]);
        Assert.Single(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateTransferAsync_rejects_missing_paths_and_empty_input()
    {
        using var service = new FileTransferService();

        var result = await service.ValidateTransferAsync(
            [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())],
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("introuvable", result.Errors[0]);
        Assert.Contains("Aucun fichier", result.Errors[1]);
    }

    [Fact]
    public async Task PrepareMetadataAsync_builds_file_and_nested_directory_metadata()
    {
        using var fixture = new TemporaryDirectory();
        var rootFile = Path.Combine(fixture.Path, "root.txt");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "source"));
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "nested"));
        var nestedFile = Path.Combine(nestedDirectory.FullName, "child.txt");
        await File.WriteAllTextAsync(rootFile, "root");
        await File.WriteAllTextAsync(nestedFile, "child");

        using var service = new FileTransferService();
        var session = await service.PrepareMetadataAsync(
            [rootFile, sourceDirectory.FullName],
            "127.0.0.1",
            4567);

        Assert.Equal("127.0.0.1", session.SenderIp);
        Assert.Equal(4567, session.SenderPort);
        Assert.Equal(2, session.TotalFiles);
        Assert.Equal(9, session.TotalSize);
        Assert.Equal(2, session.Items.Count);

        var directoryItem = Assert.Single(session.Items, item => item.IsDirectory);
        var nestedDirectoryItem = Assert.Single(directoryItem.Children);
        var childItem = Assert.Single(nestedDirectoryItem.Children);
        Assert.Equal("source/nested/child.txt", childItem.RelativePath);
        Assert.Equal(5, childItem.Size);
    }

    [Fact]
    public async Task PrepareMetadataAsync_returns_failed_session_for_invalid_input()
    {
        using var service = new FileTransferService();

        var session = await service.PrepareMetadataAsync(
            [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())],
            "127.0.0.1",
            4567);

        Assert.Equal(TransferSessionState.Failed, session.SessionState);
        Assert.Equal(TransferNegotiationStatus.Error, session.NegotiationStatus);
        Assert.NotNull(session.ErrorMessage);
    }

    [Fact]
    public async Task CancelSessionAsync_marks_prepared_session_cancelled()
    {
        using var fixture = new TemporaryDirectory();
        var filePath = Path.Combine(fixture.Path, "payload.txt");
        await File.WriteAllTextAsync(filePath, "payload");
        using var service = new FileTransferService();
        var session = await service.PrepareMetadataAsync([filePath], "127.0.0.1", 4567);

        await service.CancelSessionAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(TransferSessionState.Cancelled, session.SessionState);
        Assert.Equal(TransferNegotiationStatus.Cancelled, session.NegotiationStatus);
        Assert.Equal("Le transfert a été annulé.", session.ErrorMessage);
    }

    [Fact]
    public async Task ValidateTransferAsync_honors_cancellation()
    {
        using var service = new FileTransferService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ValidateTransferAsync([], cancellation.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wifi-core-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
