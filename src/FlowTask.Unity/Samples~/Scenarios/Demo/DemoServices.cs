using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace Katout.FlowTask.Samples
{
    /// <summary>The demo's store: it answers after a second. 3 purchases in 10 fail and can be retried; 1 in 10 is declined.</summary>
    sealed class DemoStore : IStore
    {
        readonly Random _random = new Random();
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
    sealed class DemoLoader : IAssetLoader
    {
        readonly Random _random = new Random();

        public IAssetRequest Load(string key) => new Request(key, 0.5f + 1.5f * (float)_random.NextDouble(), _random.Next(8) == 0);

        sealed class Request : IAssetRequest
        {
            readonly float _start = Time.realtimeSinceStartup;
            readonly float _seconds;
            readonly bool _fails;

            public Request(string key, float seconds, bool fails)
            {
                Key = key;
                _seconds = seconds;
                _fails = fails;
            }

            public string Key { get; }
            public float Progress => Mathf.Clamp01((Time.realtimeSinceStartup - _start) / _seconds);
            public bool IsDone => Progress >= 1;
            public Object Asset => IsDone && !_fails ? Texture2D.whiteTexture : null;

            public void Release()
            {
            }
        }
    }
}
