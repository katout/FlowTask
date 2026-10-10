using System.Collections.Generic;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="Loading"/>.</summary>
public static class LoadingSmoke
{
    /// <summary>The real loader, counting releases and seeing whether each one waited for its loading thread.</summary>
    sealed class CountingLoader : IResourceLoader
    {
        readonly ThreadedResourceLoader _loader;
        public readonly List<Counted> Requests = new();

        public CountingLoader(FlowWorld world) => _loader = new ThreadedResourceLoader(world);

        public IResourceRequest Load(string path)
        {
            var request = new Counted(_loader.Load(path));
            Requests.Add(request);
            return request;
        }

        public bool EachReleasedOnce()
        {
            foreach (var r in Requests)
            {
                if (r.Releases != 1) return false;
            }

            return Requests.Count > 0;
        }
    }

    /// <summary>Requests that stay loading until released: a cancel or a screen leaving always comes first.</summary>
    sealed class PendingLoader : IResourceLoader
    {
        public readonly List<Pending> Requests = new();

        public IResourceRequest Load(string path)
        {
            var request = new Pending(path);
            Requests.Add(request);
            return request;
        }

        public bool EachReleasedOnce() => Requests.Count > 0 && Requests.TrueForAll(r => r.Releases == 1);
    }

    sealed class Pending : IResourceRequest
    {
        public Pending(string path) => Path = path;

        public int Releases { get; private set; }
        public string Path { get; }
        public float Progress => 0.5f;
        public bool IsDone => false;
        public Resource Resource => null;
        public void Release() => Releases++;
    }

    sealed class Counted : IResourceRequest
    {
        readonly IResourceRequest _inner;

        public Counted(IResourceRequest inner) => _inner = inner;

        public int Releases { get; private set; }

        /// <summary>The loading thread was still at work when Release was called.</summary>
        public bool LoadingAtRelease { get; private set; }

        /// <summary>Release took the result itself (LoadThreadedGet, which waits for the thread).</summary>
        public bool TakenInRelease { get; private set; }

        public ulong ReleaseUsec { get; private set; }
        public string Path => _inner.Path;
        public float Progress => _inner.Progress;
        public bool IsDone => _inner.IsDone;
        public Resource Resource => _inner.Resource;

        public void Release()
        {
            Releases++;
            LoadingAtRelease = ResourceLoader.LoadThreadedGetStatus(Path) == ResourceLoader.ThreadLoadStatus.InProgress;
            var start = Time.GetTicksUsec();
            _inner.Release();
            ReleaseUsec = Time.GetTicksUsec() - start;
            TakenInRelease = IsCollected(Path);
        }
    }

    // After LoadThreadedGet, Godot no longer knows the threaded load.
    static bool IsCollected(string path) => ResourceLoader.LoadThreadedGetStatus(path) == ResourceLoader.ThreadLoadStatus.InvalidResource;

    /// <summary>A resource big enough (16 MB) that its threaded load is still running right after the request.</summary>
    static string SaveLargeResource(string name)
    {
        var path = $"user://flowtask-smoke-{OS.GetProcessId()}-{name}.res";
        var image = Image.CreateEmpty(2048, 2048, false, Image.Format.Rgba8);
        image.Fill(new Color(0.2f, 0.4f, 0.6f));
        var error = ResourceSaver.Save(image, path);
        return error == Error.Ok ? path : null;
    }

    static void Delete(string path)
    {
        if (path != null) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }

    // What LoadAll returned: the requests, or null after Cancel.
    static string Describe(IResourceRequest[] requests) => requests == null ? "null (canceled)" : requests.Length + " requests";

    static string Describe(FlowHandle<IResourceRequest[]> h) => h.Status == FlowStatus.Succeeded ? Describe(h.Result) : h.Status.ToString();

    public static async FlowTask<Verdict> Run(Node host)
    {
        var world = FlowWorldNode.Default;
        var screen = NodeLifetime.Own(new VBoxContainer { Name = "LoadingScreen" });
        var cancel = new Button { Name = "Cancel", Text = "Cancel" };
        var progress = new ProgressBar { Name = "Progress" };
        screen.AddChild(cancel);
        screen.AddChild(progress);
        host.AddChild(screen);

        // Two real resources: both reach the caller, who releases them.
        var ok = new CountingLoader(world);
        var loaded = await Loading.LoadAll(ok, new[] { "res://Handover.tscn", "res://SmokeEmitter.cs" }, cancel, progress);
        var resources = loaded != null ? loaded[0].Resource?.GetType().Name + ", " + loaded[1].Resource?.GetType().Name : "none";
        var keptForTheCaller = loaded != null && ok.Requests.TrueForAll(r => r.Releases == 0);
        if (loaded != null) foreach (var request in loaded) request.Release();

        // A missing resource: the load throws ResourceLoadException, caught here as a caller would, and the others are
        // released.
        GD.Print("[smoke] (the next ERROR lines are expected: a resource that does not exist)");
        var failing = new CountingLoader(world);
        string failedPath = null;
        try
        {
            await Loading.LoadAll(failing, new[] { "res://Handover.tscn", "res://no-such-resource.tres" }, cancel, progress);
        }
        catch (ResourceLoadException e)
        {
            failedPath = e.Path;
        }

        // Cancel while loading: canceled, everything released.
        var canceling = new PendingLoader();
        var canceled = Flow.Spawn(Loading.LoadAll(canceling, new[] { "res://a.tres", "res://b.tres" }, cancel, progress));
        await FlowTask.WaitUntil(progress, p => p.Value > 0);
        var shownProgress = progress.Value;
        cancel.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(() => canceled.IsCompleted);

        // Cancel while a real thread still loads: the request is released without waiting for the thread, and its
        // result is taken later, when the thread is done.
        var large = SaveLargeResource("cancel");
        var slow = new CountingLoader(world);
        var slowLoad = Flow.Spawn(Loading.LoadAll(slow, new[] { large }, cancel, progress));
        cancel.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(() => slowLoad.IsCompleted);
        var slowRequest = slow.Requests.Count == 1 ? slow.Requests[0] : null;
        var releasedEarly = slowRequest != null && slowRequest.LoadingAtRelease && !slowRequest.TakenInRelease;
        await FlowTask.WaitUntil(large, IsCollected);
        Delete(large);

        // The loading screen leaves the tree while loading: released too.
        var leaving = new PendingLoader();
        var doomed = new Control { Name = "DoomedLoadingScreen" };
        var doomedCancel = new Button { Name = "Cancel" };
        var doomedProgress = new ProgressBar { Name = "Progress" };
        doomed.AddChild(doomedCancel);
        doomed.AddChild(doomedProgress);
        host.AddChild(doomed);
        var bound = doomed.RunWhileInTree(Loading.LoadAll(leaving, new[] { "res://a.tres" }, doomedCancel, doomedProgress).WithoutResult());
        await FlowTask.NextFrame();
        doomed.QueueFree();
        await FlowTask.WaitUntil(() => bound.IsCompleted);

        var slowCanceled = slowLoad.Status == FlowStatus.Succeeded && slowLoad.Result == null;
        var slowDetail = slowRequest == null
            ? "no request"
            : $"loading at release {slowRequest.LoadingAtRelease}, taken in release {slowRequest.TakenInRelease}, release took {slowRequest.ReleaseUsec} us";
        return Verdict.Of($"loaded: {Describe(loaded)} ({resources}); missing: {failedPath ?? "no exception"}; cancel: {Describe(canceled)} (progress shown {shownProgress}); cancel of a real load: {Describe(slowLoad)} ({slowDetail}); screen left: {bound.Status} ({bound.Result})",
            (loaded != null && resources == "PackedScene, CSharpScript" && keptForTheCaller, "both loaded and handed to the caller"),
            (failedPath == "res://no-such-resource.tres" && failing.EachReleasedOnce(), "a missing resource failed the load and released every request"),
            (shownProgress > 0 && shownProgress < progress.MaxValue, "the progress bar showed the loads"),
            (canceled.Status == FlowStatus.Succeeded && canceled.Result == null && canceling.EachReleasedOnce(), "Cancel released every request"),
            (large != null && slowCanceled && slow.EachReleasedOnce() && releasedEarly, "Cancel of a real load released it without waiting for its thread, and it was taken later"),
            (bound.Status == FlowStatus.Succeeded && !bound.Result && leaving.EachReleasedOnce(), "leaving the tree released every request"));
    }
}
