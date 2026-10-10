using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>The demo scene's component: it starts, runs a sample and ends with its GameObject, logging no error.</summary>
    public class DemoTests
    {
        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        // Each case starts a demo, a session of its own: its GameClocks belong to the demo's flow and leave the shared World
        // with it.
        [UnityTest]
        public IEnumerator Demo_RunsASampleAndEndsWithItsObject([Values] Ending ending)
        {
            var host = SampleHost.Create("Demo", ending);
            host.GameObject.AddComponent<Demo>();
            yield return Frames(2);
            Assert.That(Object.FindAnyObjectByType<Enemy>(), Is.Not.Null, "an enemy patrols");

            host.Transform.GetComponentsInChildren<Button>().First(b => b.name == "Confirm dialog").onClick.Invoke();
            yield return null;
            var dialog = Object.FindAnyObjectByType<ConfirmDialogView>();
            Assert.That(dialog, Is.Not.Null, "the button opened the dialog");
            dialog.OkButton.onClick.Invoke();
            yield return WaitUntil(() => dialog == null, what: "the dialog to close");

            yield return host.End();
            yield return null;
            Assert.That(Object.FindAnyObjectByType<Enemy>(), Is.Null, "the enemy went with the demo");
        }
    }
}
