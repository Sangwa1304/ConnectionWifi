using System.Net;
using System.Net.Http.Json;
using System.Text;
using LibraryMI.Models;
using WindowsWifiLibrary.Core.Services;
using Xunit;

namespace WindowsWifiLibrary.Core.Tests;

public class TransferReceiverServiceTests
{
    [Fact]
    public async Task GetSessionAsync_escapes_session_id_and_deserializes_response()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(new TransferSessionMetadata { SessionId = "returned-session" }));
        using var client = new HttpClient(handler);
        var service = new TransferReceiverService(client);

        var session = await service.GetSessionAsync(
            "http://sender/",
            "session/id",
            CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("returned-session", session.SessionId);
        Assert.Equal("http://sender/api/transfer/session/session%2Fid", handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetSessionAsync_returns_null_for_unsuccessful_response()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var service = new TransferReceiverService(client);

        var session = await service.GetSessionAsync("http://sender", "session", CancellationToken.None);

        Assert.Null(session);
    }

    [Fact]
    public async Task AcceptTransferAsync_posts_payload_and_returns_decision()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(new TransferDecision { Accepted = true, DestinationFolder = "Downloads" }));
        using var client = new HttpClient(handler);
        var service = new TransferReceiverService(client);

        var decision = await service.AcceptTransferAsync(
            "http://sender/",
            "session/id",
            "Downloads",
            CancellationToken.None);

        Assert.NotNull(decision);
        Assert.True(decision.Accepted);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://sender/api/transfer/accept", handler.RequestUri!.ToString());
        Assert.Contains("\"sessionId\":\"session/id\"", handler.RequestBody);
    }

    [Fact]
    public async Task DownloadSessionAsync_downloads_nested_files_and_reports_progress()
    {
        using var destination = new TemporaryDirectory();
        var content = Encoding.UTF8.GetBytes("hello");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        });
        using var client = new HttpClient(handler);
        var service = new TransferReceiverService(client);
        var session = new TransferSessionMetadata
        {
            SessionId = "session",
            TotalSize = content.Length,
            Items =
            [
                new TransferItemMetadata
                {
                    RelativePath = "folder",
                    IsDirectory = true,
                    Children =
                    [
                        new TransferItemMetadata
                        {
                            RelativePath = "folder/file.txt",
                            Size = content.Length
                        }
                    ]
                }
            ]
        };
        var progressValues = new List<FileTransferProgress>();

        await service.DownloadSessionAsync(
            "http://sender",
            session,
            destination.Path,
            new Progress<FileTransferProgress>(progressValues.Add),
            CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(destination.Path, "folder", "file.txt")));
        Assert.NotEmpty(progressValues);
        Assert.Equal(content.Length, progressValues[^1].BytesTransferred);
        Assert.Equal("folder/file.txt", progressValues[^1].CurrentFileName);
        Assert.Contains("path=folder%2Ffile.txt", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadSessionAsync_rejects_null_session()
    {
        var service = new TransferReceiverService(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage())));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.DownloadSessionAsync(
                "http://sender",
                null!,
                Path.GetTempPath(),
                new Progress<FileTransferProgress>(),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadSessionAsync_rejects_path_traversal_before_request()
    {
        using var destination = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("A network request was not expected."));
        using var client = new HttpClient(handler);
        var service = new TransferReceiverService(client);
        var session = new TransferSessionMetadata
        {
                SessionId = "session",
                Items = [new TransferItemMetadata { RelativePath = "../outside.txt", Size = 1 }]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadSessionAsync(
                    "http://sender",
                    session,
                    destination.Path,
                    new Progress<FileTransferProgress>(),
                    CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
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
