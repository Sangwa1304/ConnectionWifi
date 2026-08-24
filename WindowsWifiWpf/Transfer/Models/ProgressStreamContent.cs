using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WindowsWifiWpf.Transfer.Models;

namespace WindowsWifiWpf.Transfer.Models;

internal class ProgressStreamContent : HttpContent 
{ 
    private readonly Stream _stream; 
    private readonly int _bufferSize; 
    private readonly IProgress<FileTransferProgress>? _progress; 
    private readonly CancellationToken _cancellationToken; 

    public ProgressStreamContent(Stream stream, int bufferSize, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) 
    { 
        _stream = stream; 
        _bufferSize = bufferSize > 0 ? bufferSize : 81920; 
        _progress = progress; 
        _cancellationToken = cancellationToken; 
    } 

    protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context) 
    { 
        var totalBytes = _stream.CanSeek ? _stream.Length : -1; 
        var buffer = new byte[_bufferSize]; 
        long totalBytesRead = 0; 
        int bytesRead; 

        while ((bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, 
_cancellationToken).ConfigureAwait(false)) > 0) 
        { 
            _cancellationToken.ThrowIfCancellationRequested(); 
            await destination.WriteAsync(buffer, 0, bytesRead, _cancellationToken).ConfigureAwait(false); 
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
