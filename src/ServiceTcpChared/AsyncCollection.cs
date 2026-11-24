using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ServiceTcpChared;

public sealed class AsyncCollection<T>
{
    private readonly Channel<T> _channel =
        Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public Task AddAsync(T item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException(
                "Impossible d'ajouter un élément : la collection est fermée.");
        }

        return Task.CompletedTask;
    }

    public Task AddAsync(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (T item in items)
        {
            if (!_channel.Writer.TryWrite(item))
            {
                throw new InvalidOperationException(
                    "Impossible d'ajouter un élément : la collection est fermée.");
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<T> GetAsyncCollection(
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (T item in _channel.Reader
            .ReadAllAsync(token)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal void CompleteAdding()
    {
        _channel.Writer.TryComplete();
    }

    internal IEnumerable<T> GetCollection()
    {
        List<T> items = [];

        while (_channel.Reader.TryRead(out T? item))
        {
            items.Add(item);
        }

        return items;
    }
}