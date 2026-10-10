using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>How a game ends the GameObject a sample's flow is bound to.</summary>
    public enum Ending
    {
        /// <summary>
        /// Object.Destroy: Unity deactivates the object inside the call (OnDisable), which cancels the flow and flushes
        /// its World, so it unwinds there, before the object is destroyed at the end of the frame.
        /// </summary>
        Destroy,

        /// <summary>SetActive(false), as a pool does: the flow unwinds inside the call, and the object stays.</summary>
        Deactivate,

        /// <summary>The object's scene is unloaded (a scene change): its objects are deactivated and destroyed, as with Destroy.</summary>
        SceneUnload,
    }

    /// <summary>The GameObject a sample runs on, made for one <see cref="Ending"/>. <see cref="CleanUp"/> removes what a test left.</summary>
    public sealed class SampleHost
    {
        static readonly List<SampleHost> s_live = new List<SampleHost>();
        static int s_scenes;

        readonly Scene _scene;
        readonly Ending _ending;

        SampleHost(GameObject gameObject, Scene scene, Ending ending)
        {
            GameObject = gameObject;
            _scene = scene;
            _ending = ending;
        }

        public GameObject GameObject { get; }
        public Transform Transform => GameObject.transform;

        /// <summary>In a scene of its own for <see cref="Ending.SceneUnload"/>.</summary>
        public static SampleHost Create(string name, Ending ending)
        {
            var go = new GameObject(name);
            var scene = default(Scene);
            if (ending == Ending.SceneUnload)
            {
                // Made at run time, so the test needs no scene in the build settings and runs in players too.
                scene = SceneManager.CreateScene("FlowTaskSample" + ++s_scenes);
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            var host = new SampleHost(go, scene, ending);
            s_live.Add(host);
            return host;
        }

        /// <summary>
        /// Ends the host the way its <see cref="Ending"/> says, and returns once Unity has destroyed it (a deactivated host
        /// stays, and <see cref="CleanUp"/> destroys it).
        /// </summary>
        public IEnumerator End()
        {
            if (_ending == Ending.Deactivate)
            {
                GameObject.SetActive(false);
                yield return null;
                yield break;
            }

            s_live.Remove(this);
            if (_scene.IsValid())
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
            else
            {
                Object.Destroy(GameObject);
                yield return null; // Destroy takes effect at the end of the frame
            }
        }

        /// <summary>Destroys the hosts a test did not end (a failed test) or only deactivated, so the next test starts clean.</summary>
        public static IEnumerator CleanUp()
        {
            var left = s_live.ToArray();
            s_live.Clear();
            foreach (var host in left)
            {
                if (host._scene.IsValid() && host._scene.isLoaded) yield return SceneManager.UnloadSceneAsync(host._scene);
                else if (host.GameObject != null) Object.Destroy(host.GameObject);
            }

            yield return null;
        }
    }
}
