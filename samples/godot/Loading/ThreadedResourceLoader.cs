using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>A request for one resource (<see cref="ThreadedResourceLoader"/> is the ResourceLoader version).</summary>
public interface IResourceRequest
{
    string Path { get; }
    float Progress { get; }
    bool IsDone { get; }

    /// <summary>The loaded resource; null when loading failed.</summary>
    Resource Resource { get; }

    /// <summary>Hands the request back to the loader, without waiting for a load still in progress. Called once.</summary>
    void Release();
}

public interface IResourceLoader
{
    IResourceRequest Load(string path);
}

/// <summary>
/// <see cref="IResourceLoader"/> over <c>ResourceLoader.LoadThreadedRequest</c>. A threaded load is handed back by
/// taking its result (<c>LoadThreadedGet</c>), which blocks the main thread until the load ends. So a request released
/// while its thread still loads is not taken there: a flow of its own, on the World given to the constructor, takes it
/// once the thread is done, and Release returns at once (a Cancel button or a screen leaving the tree does not freeze
/// the game).
/// </summary>
public sealed class ThreadedResourceLoader : IResourceLoader
{
    readonly FlowWorld _world;

    /// <param name="world">Runs the takes of loads released while in progress (the session's World).</param>
    public ThreadedResourceLoader(FlowWorld world) => _world = world;

    public IResourceRequest Load(string path) => new Request(path, _world);

    sealed class Request : IResourceRequest
    {
        readonly global::Godot.Collections.Array _progress = new();
        readonly FlowWorld _world;
        readonly bool _started;
        Resource _resource;
        bool _taken;

        public Request(string path, FlowWorld world)
        {
            Path = path;
            _world = world;
            _started = ResourceLoader.LoadThreadedRequest(path) == Error.Ok;
        }

        public string Path { get; }

        ResourceLoader.ThreadLoadStatus Status =>
            _started && !_taken ? ResourceLoader.LoadThreadedGetStatus(Path, _progress) : ResourceLoader.ThreadLoadStatus.Failed;

        // One status query: it fills _progress while the load is in progress.
        public float Progress =>
            Status != ResourceLoader.ThreadLoadStatus.InProgress ? 1 : _progress.Count > 0 ? _progress[0].AsSingle() : 0;

        public bool IsDone => _taken || Status != ResourceLoader.ThreadLoadStatus.InProgress;

        public Resource Resource
        {
            get
            {
                if (!_taken && _started && Status == ResourceLoader.ThreadLoadStatus.Loaded)
                {
                    _taken = true;
                    _resource = ResourceLoader.LoadThreadedGet(Path);
                }

                return _resource;
            }
        }

        public void Release()
        {
            _resource = null;
            if (_taken || !_started)
            {
                _taken = true;
                return;
            }

            _taken = true;
            if (ResourceLoader.LoadThreadedGetStatus(Path) == ResourceLoader.ThreadLoadStatus.InProgress)
            {
                // Taking it now would wait for the loading thread. A root flow of its own takes it when the thread is
                // done (the caller's flow may be ending); a World disposed first ends that flow, and the load with the game.
                _world.Run(TakeWhenDone(Path));
            }
            else
            {
                ResourceLoader.LoadThreadedGet(Path); // loaded or failed: returns at once
            }
        }

        static async FlowTask TakeWhenDone(string path)
        {
            await FlowTask.WaitUntil(path, p => ResourceLoader.LoadThreadedGetStatus(p) != ResourceLoader.ThreadLoadStatus.InProgress);
            ResourceLoader.LoadThreadedGet(path);
        }
    }
}
