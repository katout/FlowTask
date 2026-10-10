using UnityEngine;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// A request for one asset: an Addressables handle, an AssetBundle request or <see cref="Resources.LoadAsync(string)"/>
    /// behind one small interface (<see cref="ResourcesLoader"/> is the Resources version).
    /// </summary>
    public interface IAssetRequest
    {
        string Key { get; }
        float Progress { get; }
        bool IsDone { get; }

        /// <summary>The loaded asset; null when loading failed.</summary>
        Object Asset { get; }

        /// <summary>
        /// Hands the request back: the loader frees the asset (or the request when it is still loading) once nothing else
        /// uses it. Called once.
        /// </summary>
        void Release();
    }

    public interface IAssetLoader
    {
        IAssetRequest Load(string key);
    }

    /// <summary><see cref="IAssetLoader"/> over <c>Resources.LoadAsync</c>.</summary>
    public sealed class ResourcesLoader : IAssetLoader
    {
        public IAssetRequest Load(string key) => new Request(key, Resources.LoadAsync(key));

        sealed class Request : IAssetRequest
        {
            readonly ResourceRequest _request;

            public Request(string key, ResourceRequest request)
            {
                Key = key;
                _request = request;
            }

            public string Key { get; }
            public float Progress => _request.progress;
            public bool IsDone => _request.isDone;
            public Object Asset => _request.isDone ? _request.asset : null;

            // Resources counts no references: Resources.UnloadAsset here would unload the asset from under everyone else
            // who loaded it too (they would read it from disk again). Dropping the request is enough: the asset is freed by
            // Resources.UnloadUnusedAssets (on a scene change, or when the game calls it) once nothing uses it. A loader
            // that counts references, such as Addressables (Addressables.Release), frees it here.
            public void Release()
            {
            }
        }
    }
}
