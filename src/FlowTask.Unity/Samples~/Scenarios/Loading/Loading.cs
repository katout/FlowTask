using System;
using System.Collections.Generic;
using Katout.FlowTask.Unity;

namespace Katout.FlowTask.Samples
{
    /// <summary>An asset could not be loaded: an expected failure, which the loading screen shows.</summary>
    public sealed class AssetLoadException : Exception
    {
        public AssetLoadException(string key)
            : base($"{key} could not be loaded")
        {
            Key = key;
        }

        public string Key { get; }
    }

    /// <summary>
    /// Sample: loads several assets in parallel with one progress value and a Cancel button. The first failure
    /// stops the other loads; on failure, cancel, or the loading screen going away, every request is released.
    /// </summary>
    public static class Loading
    {
        /// <summary>
        /// Returns the requests, which the caller owns from then on (and releases when done with the assets); null when the
        /// player pressed Cancel. When an asset cannot be loaded, the other loads stop, <see cref="LoadingView.Error"/>
        /// names the asset, and <see cref="AssetLoadException"/> is thrown. Every request that does not reach the caller is
        /// released here, the ones still loading included.
        /// </summary>
        public static async FlowTask<IAssetRequest[]> LoadAll(IAssetLoader loader, IReadOnlyList<string> keys, LoadingView view)
        {
            using var cancel = view.CancelButton.ClickedSignal();
            var requests = new List<IAssetRequest>(keys.Count);
            var handedOver = false;
            try
            {
                var loads = new FlowTask<IAssetRequest>[keys.Count];
                for (var i = 0; i < keys.Count; i++)
                {
                    var request = loader.Load(keys[i]);
                    requests.Add(request);
                    loads[i] = WaitLoaded(request);
                }

                // WhenAll stops at the first failure and unwinds the other loads; Cancel unwinds all of it, the progress too.
                var r = await FlowTask.Race(FlowTask.WhenAll(loads), cancel.Next(), ShowProgress(requests, view));
                if (r.Index == 1) return null;

                view.Progress = 1;
                handedOver = true;
                return r.Value0;
            }
            catch (AssetLoadException e)
            {
                view.Error = e.Message; // the screen names the asset; the caller decides what comes next
                throw;
            }
            finally
            {
                // Every request that does not reach the caller: on failure, on cancel, and when the flow is canceled from
                // outside (the loading screen destroyed with its scene).
                if (!handedOver)
                {
                    foreach (var request in requests) request.Release();
                }
            }
        }

        static async FlowTask<IAssetRequest> WaitLoaded(IAssetRequest request)
        {
            await FlowTask.WaitUntil(request, r => r.IsDone);
            if (request.Asset == null) throw new AssetLoadException(request.Key);
            return request;
        }

        static async FlowTask ShowProgress(List<IAssetRequest> requests, LoadingView view)
        {
            while (true)
            {
                var sum = 0f;
                foreach (var request in requests) sum += request.Progress;
                view.Progress = requests.Count == 0 ? 1 : sum / requests.Count;
                await FlowTask.NextFrame();
            }
        }
    }
}
