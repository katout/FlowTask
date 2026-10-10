using System;
using UnityEngine;
#if FLOWTASK_SAMPLES_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: reads the back key (Escape; Android's back button arrives as Escape) in Update and hands it to a
    /// <see cref="BackKeyRouter"/>. The flow that owns the key resumes at the flush point at the end of Update, in the same
    /// frame. Put it on one GameObject of the session and set <see cref="Router"/> when the session starts. It reads the
    /// Input System package when the project has it and uses it, the Input Manager otherwise.
    /// </summary>
    public sealed class BackKeyInput : MonoBehaviour
    {
        /// <summary>The router of the current session.</summary>
        public BackKeyRouter Router { get; set; }

        /// <summary>A press that reached no entry: nothing on screen owns the key (e.g. ask whether to quit).</summary>
        public event Action Unhandled;

        void Update()
        {
            if (Router == null || !WasBackPressed()) return;
            if (!Router.Press()) Unhandled?.Invoke();
        }

        static bool WasBackPressed()
        {
#if FLOWTASK_SAMPLES_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Escape);
#else
            return false;
#endif
        }
    }
}
