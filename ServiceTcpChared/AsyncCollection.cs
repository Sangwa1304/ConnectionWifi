using System.Runtime.CompilerServices;

namespace ServiceTcpChared;

public class AsyncCollection<T> 
{
    CancellationTokenSource? cts = null;
    private bool NoAdded = false;
    private event Action? StopAdded;
    private event Action? Added;
    private bool Completed => _Collection.Count == 0;
    private readonly List<T> _Collection = [];
    private List<T> Collection { get => _Collection; }
    public event Action<CancellationToken>? NewWord;

    public AsyncCollection()
    {
        Added += () => cts?.Cancel();
        StopAdded += () => cts?.Cancel();
    }
    public async Task AddAsync(T item)
    {
        
        if (NoAdded) return;
        Added?.Invoke();
        _Collection.Add(item);
        cts = new();
        await Task.Delay(100);
        if (cts?.IsCancellationRequested?? false) return;
        
        NewWord?.Invoke(cts?.Token?? default);
    }
    public async Task AddAsync(IEnumerable<T> items)
    {

        if (NoAdded) return;
        Added?.Invoke();
        _Collection.AddRange(items);
        cts = new();
        await Task.Delay(100);
        if (cts?.IsCancellationRequested ?? false) return;

        NewWord?.Invoke(cts?.Token ?? default);
    }
    public async IAsyncEnumerable<T> GetAsyncCollection([EnumeratorCancellation] CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Collection.Count != 0) 
            { 
                var r = Collection.First();
                _Collection.Remove(r);

                yield return r;
            }
            else
            {

                if (NoAdded) break;
            }
            cts = new();
            await Task.Delay(5000,cts.Token);
        }
        CompleteAdding();

    }

    private async Task AwaitChange(CancellationToken token)
    {
        if(token.IsCancellationRequested)return;
        while(!token.IsCancellationRequested)
        {
            await Task.Delay(1000, token);
        }
    }

    internal void CompleteAdding()
    {
        NoAdded = true;
        StopAdded?.Invoke();
    }

    internal IEnumerable<T> GetCollection()
    {
        List<T> m = new();
        foreach(var collecte in _Collection)
        {
            m.Add(collecte);
        }
        _Collection.Clear();
        return m;
        
    }
}
