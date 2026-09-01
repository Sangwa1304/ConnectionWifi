using System.Net;

namespace WindowsWifiLibrary.Core.Models;

internal class ProgressStreamContent(Stream stream, int bufferSize, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) : HttpContent
{
    private readonly Stream _stream = stream;
    private readonly int _bufferSize = bufferSize > 0 ? bufferSize : 81920;
    private readonly IProgress<FileTransferProgress>? _progress = progress;
    private readonly CancellationToken _cancellationToken = cancellationToken;

    protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context)
    {
        var totalBytes = _stream.CanSeek ? _stream.Length : -1;
        var buffer = new byte[_bufferSize];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await _stream.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false)) > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), _cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;

            _progress?.Report(new FileTransferProgress
            {
                BytesTransferred = totalBytesRead,
                TotalBytes = totalBytes
            });
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_stream.CanSeek)
        {
            length = _stream.Length;
            return true;
        }
        length = 0;
        return false;
    }
}
