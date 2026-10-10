using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>The demo's store: it answers after a second. 3 purchases in 10 fail and can be retried; 1 in 10 is declined.</summary>
sealed class DemoStore : IStore
{
    readonly Random _random = new();
    int _orders;

    public async Task<Receipt> PurchaseAsync(string itemId, CancellationToken ct)
    {
        await Task.Delay(1000, ct);
        var roll = _random.Next(10);
        if (roll < 3) throw new StoreException("the store is busy", retryable: true);
        if (roll == 3) throw new StoreException("payment declined", retryable: false);
        return new Receipt(itemId, "demo-" + ++_orders);
    }
}

/// <summary>The demo's settings file: a write takes half a second.</summary>
sealed class DemoSettingsStore : ISettingsStore
{
    readonly Action<string> _log;

    public DemoSettingsStore(Action<string> log) => _log = log;

    public async Task SaveAsync(float volume, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        _log($"Settings: saved (volume {volume:P0})");
    }
}

/// <summary>The demo's loads: each takes 0.5 to 2 seconds, and 1 in 8 fails.</summary>
sealed class DemoLoader : IResourceLoader
{
    readonly Random _random = new();

    public IResourceRequest Load(string path) => new Request(path, 500 + (ulong)_random.Next(1500), _random.Next(8) == 0);

    sealed class Request : IResourceRequest
    {
        readonly ulong _start = Time.GetTicksMsec();
        readonly ulong _milliseconds;
        readonly Resource _resource;

        public Request(string path, ulong milliseconds, bool fails)
        {
            Path = path;
            _milliseconds = milliseconds;
            _resource = fails ? null : new Resource();
        }

        public string Path { get; }
        public float Progress => Math.Min(1f, (float)(Time.GetTicksMsec() - _start) / _milliseconds);
        public bool IsDone => Progress >= 1;
        public Resource Resource => IsDone ? _resource : null;

        public void Release()
        {
        }
    }
}
