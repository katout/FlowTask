using System;
using System.Collections.Generic;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: routes the back key (Escape, Android's back button) to the flow that owns it. The
    /// entry on the highest layer gets the press; within a layer, the one pushed last. A flow waits with
    /// <c>await router.Next(BackKeyRouter.Dialog)</c> (usually as a branch of a Race), and its entry leaves with the wait:
    /// on the press, when the Race is lost, when the flow is canceled or its GameObject destroyed.
    /// <para>
    /// A composition of a list, <see cref="Flow.Own{T}"/> and <see cref="Signal{T}"/>, not a library API: copy it and
    /// change the layers. It uses no engine API. Make one per session (World) and pass it to the flows that use it; do not
    /// keep it in a static field.
    /// </para>
    /// </summary>
    public sealed class BackKeyRouter
    {
        public const int Screen = 0;
        public const int Dialog = 100;
        public const int System = 200;
        public const int Critical = 300;

        // The lowest layer first; within a layer, in the order pushed. The last entry gets the press.
        readonly List<Entry> _entries = new();

        // Nobody ever waits on this signal. An entry that holds it on top swallows the key: see Block.
        readonly Signal<FlowUnit> _swallowed = new("BackKey.Blocked");

        /// <summary>Entries on the stack (for tests and debug displays).</summary>
        public int Count => _entries.Count;

        /// <summary>True while an entry at <paramref name="layer"/> or above is on the stack.</summary>
        public bool IsCaptured(int layer) => _entries.Count > 0 && _entries[^1].Layer >= layer;

        /// <summary>
        /// Waits for the next press routed to <paramref name="layer"/>. The entry is pushed when the wait starts and removed
        /// when it ends, however it ends.
        /// </summary>
        public async FlowTask Next(int layer)
        {
            var pressed = new Signal<FlowUnit>("BackKey");
            using var entry = Push(layer, pressed);
            await pressed.Next();
        }

        /// <summary>
        /// Swallows the key until the handle is disposed (<c>using var block = router.Block();</c>), e.g. while a purchase
        /// talks to the store. The idiom: an entry that nobody waits on, on the top layer, takes every press and drops it.
        /// Taken in flow code, the handle is also released when the scope ends, and it stays in that scope's cleanup
        /// list until then: take it in a method of its own (as Shop.Purchase does), not in a loop of a long-lived scope.
        /// </summary>
        public IDisposable Block() => Push(Critical, _swallowed);

        /// <summary>
        /// Delivers a press to the top entry: its flow resumes at the next Tick or flush point of its World, so a press from
        /// MonoBehaviour.Update (<see cref="BackKeyInput"/>) resumes it in the same frame. Returns false when no entry is
        /// on the stack, where a game asks whether to quit, for example. A press that a <see cref="Block"/> swallows
        /// returns true.
        /// </summary>
        public bool Press()
        {
            if (_entries.Count == 0) return false;
            _entries[^1].Pressed.Emit(FlowUnit.Default);
            return true;
        }

        // Puts the entry above the others of its layer. The current scope owns it (Flow.Own) and disposes it when it ends;
        // outside a flow, the caller disposes it.
        Entry Push(int layer, Signal<FlowUnit> pressed)
        {
            var index = _entries.Count;
            while (index > 0 && _entries[index - 1].Layer > layer) index--;
            var entry = new Entry(this, layer, pressed);
            _entries.Insert(index, entry);
            return Flow.Own(entry);
        }

        sealed class Entry : IDisposable
        {
            readonly BackKeyRouter _router;

            public Entry(BackKeyRouter router, int layer, Signal<FlowUnit> pressed)
            {
                _router = router;
                Layer = layer;
                Pressed = pressed;
            }

            public int Layer { get; }
            public Signal<FlowUnit> Pressed { get; }

            // A using disposes it, and its scope disposes it again when it ends: the second Remove finds nothing.
            public void Dispose() => _router._entries.Remove(this);
        }
    }
}
