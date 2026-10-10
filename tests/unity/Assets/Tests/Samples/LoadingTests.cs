using System.Collections;
using System.Collections.Generic;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>A loader whose requests the test completes, and which counts their releases.</summary>
    public sealed class FakeLoader : IAssetLoader
    {
        public readonly List<FakeRequest> Requests = new List<FakeRequest>();

        public IAssetRequest Load(string key)
        {
            var request = new FakeRequest(key);
            Requests.Add(request);
            return request;
        }
    }

    public sealed class FakeRequest : IAssetRequest
    {
        public FakeRequest(string key) => Key = key;

        public string Key { get; }
        public float Progress { get; set; }
        public bool IsDone { get; private set; }
        public Object Asset { get; private set; }
        public int Releases { get; private set; }

        public void Succeed()
        {
            Progress = 1;
            Asset = new TextAsset(Key);
            IsDone = true;
        }

        public void Fail() => IsDone = true;

        public void Release() => Releases++;
    }

    /// <summary>Tests of the <see cref="Loading"/> sample.</summary>
    public class LoadingTests
    {
        static readonly string[] Keys = { "stage", "enemies", "music" };

        FakeLoader _loader;
        AssetLoadException _failure;

        [SetUp]
        public void CreateLoader()
        {
            _loader = new FakeLoader();
            _failure = null;
        }

        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        // LoadAll as a caller runs it, with the expected failure caught (into _failure; the result is then null): an
        // exception that ended the root flow would reach the World's OnUnhandledException, which the default handler logs as an error
        // (failing the test).
        async FlowTask<IAssetRequest[]> LoadAllCatchingFailure(IAssetLoader loader, IReadOnlyList<string> keys, LoadingView view)
        {
            try
            {
                return await Loading.LoadAll(loader, keys, view);
            }
            catch (AssetLoadException e)
            {
                _failure = e;
                return null;
            }
        }

        FlowHandle<IAssetRequest[]> Start(SampleHost host, out LoadingView view)
        {
            view = LoadingView.Create(host.Transform);
            return host.GameObject.RunWhileActive(LoadAllCatchingFailure(_loader, Keys, view));
        }

        void AssertEachReleasedOnce()
        {
            Assert.That(_loader.Requests, Has.Count.EqualTo(Keys.Length));
            foreach (var request in _loader.Requests) Assert.That(request.Releases, Is.EqualTo(1), request.Key + " released once");
        }

        [UnityTest]
        public IEnumerator Loading_EveryAssetLoadedIsHandedToTheCaller()
        {
            var h = Start(SampleHost.Create("LoadingScreen", Ending.Destroy), out var view);
            _loader.Requests[0].Progress = 0.5f;
            yield return WaitUntil(() => view.Progress > 0.1f, what: "the progress");
            Assert.That(view.Progress, Is.EqualTo(0.5f / 3).Within(1e-5));

            foreach (var request in _loader.Requests) request.Succeed();
            yield return WaitFor(h);
            Assert.That(_failure, Is.Null);
            Assert.That(h.Result, Is.EqualTo(_loader.Requests.ToArray()), "in the order of the keys");
            Assert.That(view.Progress, Is.EqualTo(1f));
            foreach (var request in _loader.Requests) Assert.That(request.Releases, Is.Zero, "the caller owns them now");
        }

        [UnityTest]
        public IEnumerator Loading_AFailureStopsTheOtherLoadsAndReleasesEveryRequest()
        {
            var h = Start(SampleHost.Create("LoadingScreen", Ending.Destroy), out var view);
            _loader.Requests[0].Succeed();
            _loader.Requests[1].Fail(); // the third one is still loading
            yield return WaitFor(h);
            Assert.That(_failure?.Key, Is.EqualTo("enemies"), "AssetLoadException names the asset");
            Assert.That(view.Error, Is.EqualTo("enemies could not be loaded"));
            AssertEachReleasedOnce();
        }

        [UnityTest]
        public IEnumerator Loading_OfTwoFailuresInOneFrameTheFirstReachesTheCallerAndTheOtherStopsBeforeItThrows()
        {
            // The loads throw in the flow after their wait ends, so the first exception stops WhenAll, and the other load
            // is unwound before it resumes: there is no second exception, and nothing is reported as Undelivered.
            var undelivered = 0;
            void Count(string message, string stackTrace, LogType type)
            {
                if (message.Contains("Undelivered")) undelivered++;
            }

            Application.logMessageReceived += Count;
            try
            {
                var h = Start(SampleHost.Create("LoadingScreen", Ending.Destroy), out _);
                _loader.Requests[1].Fail();
                _loader.Requests[2].Fail();
                yield return WaitFor(h);
                yield return null;
                Assert.That(_failure?.Key, Is.EqualTo("enemies"), "the first failure, in the order the loads began");
                Assert.That(undelivered, Is.Zero);
                AssertEachReleasedOnce();
            }
            finally
            {
                Application.logMessageReceived -= Count;
            }
        }

        [UnityTest]
        public IEnumerator Loading_CancelReleasesEveryRequest()
        {
            var h = Start(SampleHost.Create("LoadingScreen", Ending.Destroy), out var view);
            _loader.Requests[0].Succeed();
            yield return null;
            view.CancelButton.onClick.Invoke();
            yield return WaitFor(h);
            Assert.That(_failure, Is.Null);
            Assert.That(h.Result, Is.Null, "Cancel returns null");
            AssertEachReleasedOnce();
        }

        [UnityTest]
        public IEnumerator Loading_EndsWithTheObjectItIsBoundTo([Values] Ending ending)
        {
            var host = SampleHost.Create("LoadingScreen", ending);
            var h = Start(host, out _);
            _loader.Requests[2].Succeed();
            yield return null;

            yield return host.End();
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            AssertEachReleasedOnce();
        }

        [UnityTest]
        public IEnumerator Loading_ResourcesLoaderLoadsFromResourcesAndReportsAMissingAsset()
        {
            var host = SampleHost.Create("LoadingScreen", Ending.Destroy);
            var view = LoadingView.Create(host.Transform);
            var ok = host.GameObject.RunWhileActive(LoadAllCatchingFailure(new ResourcesLoader(), new[] { "flowtask-resource" }, view));
            yield return WaitFor(ok, 10);
            Assert.That(_failure, Is.Null);
            Assert.That(ok.Result[0].Asset, Is.InstanceOf<TextAsset>());
            ok.Result[0].Release();

            var missing = host.GameObject.RunWhileActive(LoadAllCatchingFailure(new ResourcesLoader(), new[] { "flowtask-resource", "no-such-asset" }, view));
            yield return WaitFor(missing, 10);
            Assert.That(_failure?.Key, Is.EqualTo("no-such-asset"));
            Assert.That(view.Error, Is.EqualTo("no-such-asset could not be loaded"));
        }
    }
}
