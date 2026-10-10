using System;
using System.Collections.Generic;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>A resource could not be loaded: an expected failure, which the caller shows.</summary>
public sealed class ResourceLoadException : Exception
{
    public ResourceLoadException(string path)
        : base($"{path} could not be loaded")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Sample: loads several resources in parallel with one progress value and a Cancel button. The first
/// failure stops the other loads; on failure, cancel, or the loading screen leaving the tree, every request is released.
/// </summary>
public static class Loading
{
    /// <summary>
    /// Returns the requests, which the caller owns from then on (and releases); null when the player pressed Cancel. When a
    /// resource cannot be loaded, the other loads stop and <see cref="ResourceLoadException"/> is thrown. Every request
    /// that does not reach the caller is released here.
    /// </summary>
    public static async FlowTask<IResourceRequest[]> LoadAll(IResourceLoader loader, IReadOnlyList<string> paths, Button cancel, ProgressBar progress)
    {
        using var canceled = cancel.PressedSignal();
        var requests = new List<IResourceRequest>(paths.Count);
        var handedOver = false;
        try
        {
            var loads = new FlowTask<IResourceRequest>[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                var request = loader.Load(paths[i]);
                requests.Add(request);
                loads[i] = WaitLoaded(request);
            }

            // WhenAll stops at the first failure and unwinds the other loads; Cancel unwinds all of it, the progress too.
            var r = await FlowTask.Race(FlowTask.WhenAll(loads), canceled.Next(), ShowProgress(requests, progress));
            if (r.Index == 1) return null;

            progress.Value = progress.MaxValue;
            handedOver = true;
            return r.Value0;
        }
        finally
        {
            // Every request that does not reach the caller: on failure, on cancel, and when the flow is canceled from
            // outside (the loading screen leaving the tree).
            if (!handedOver)
            {
                foreach (var request in requests) request.Release();
            }
        }
    }

    static async FlowTask<IResourceRequest> WaitLoaded(IResourceRequest request)
    {
        await FlowTask.WaitUntil(request, r => r.IsDone);
        if (request.Resource == null) throw new ResourceLoadException(request.Path);
        return request;
    }

    static async FlowTask ShowProgress(List<IResourceRequest> requests, ProgressBar progress)
    {
        while (true)
        {
            var sum = 0f;
            foreach (var request in requests) sum += request.Progress;
            progress.Value = requests.Count == 0 ? progress.MaxValue : progress.MaxValue * sum / requests.Count;
            await FlowTask.NextFrame();
        }
    }
}
