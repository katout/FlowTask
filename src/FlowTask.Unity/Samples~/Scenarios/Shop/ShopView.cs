using System;
using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>Sample view of <see cref="ShopScreen"/>: a Buy button and a status line, built in code.</summary>
    public sealed class ShopView : MonoBehaviour
    {
        public string ItemId = "gem_pack";

        Text _status;
        string _statusText = "";

        public Button BuyButton { get; private set; }

        public string Status
        {
            get => _statusText;
            set
            {
                _statusText = value;
                if (_status != null) _status.text = value;
            }
        }

        /// <summary>The last purchase that failed with a bug (what a crash reporter would receive).</summary>
        public Exception LastBug { get; set; }

        public static ShopView Create(Transform parent)
        {
            var panel = SampleUi.Panel(parent, "Shop", 360);
            var view = panel.gameObject.AddComponent<ShopView>();
            SampleUi.Label(panel, "Gem pack (the back key leaves)").alignment = TextAnchor.MiddleCenter;
            view.BuyButton = SampleUi.Button(panel, "Buy");
            view._status = SampleUi.Label(panel, "");
            return view;
        }
    }
}
