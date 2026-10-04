namespace ServiceTcpChared;

class AsyncCollection<T> 
{
    CancellationTokenSource? cts = null;
    private bool NoAdded = false;
    private event Action? StopAdded;
    private event Action? Added;
    private bool Completed => _Collection.Count == 0;
    private List<T> _Collection = [];

    public AsyncCollection()
    {
        Added += () => cts?.Cancel();
        StopAdded += () => cts?.Cancel();
    }

    private List<T> Collection { get => _Collection; }
    public event Action<CancellationToken> NewWord;

    public async Task Add(T item)
    {
        
        if (NoAdded) return;
        Added?.Invoke();
        _Collection.Add(item);
        cts = new();
        await Task.Delay(100);
        if (cts?.IsCancellationRequested?? false) return;
        
        NewWord?.Invoke(cts.Token);
    }
    public async IAsyncEnumerable<T> GetAsyncCollection()
    {
        while (true)
        {
            foreach (var r in Collection)
            {
                yield return r;
                _Collection.Remove(r);
            }
            
            if(!Completed)
                    continue;
            if (NoAdded) break;

            cts = new();
            await Task.Delay(10000,cts.Token);
        }

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
