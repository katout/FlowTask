namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW009: Flow.Spawn in an event handler written in a FlowTask method.</summary>
public class Flow009Tests
{
    const string Button = """

        public sealed class Button
        {
            public event System.Action Clicked;
            public event System.EventHandler Pressed;
            public void Click() { Clicked?.Invoke(); Pressed?.Invoke(this, System.EventArgs.Empty); }
        }
        """;

    [Test]
    public Task FLOW009_SpawnInAnEventHandlerLambdaIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using System.Threading.Tasks;
        using Katout.FlowTask;
        using static Katout.FlowTask.Flow;

        class C
        {
            static async FlowTask OpenShop() { await FlowTask.NextFrame(); }

            async FlowTask Menu(Button button)
            {
                button.Clicked += () => {|FLOW009:Flow.Spawn(OpenShop())|};
                button.Clicked += delegate { {|FLOW009:Flow.Spawn(OpenShop())|}; };
                button.Pressed += (sender, e) => {|FLOW009:Spawn(OpenShop())|};
                button.Clicked += async () =>
                {
                    {|FLOW009:Flow.Spawn(OpenShop())|};
                    await Task.Delay(1);
                };

                void OnClicked() => {|FLOW009:Flow.Spawn(OpenShop())|};
                button.Clicked += OnClicked;

                Action handler = () => {|FLOW009:Flow.Spawn(OpenShop())|};
                button.Clicked += handler;

                await FlowTask.NextFrame();
            }
        }
        """ + Button,
        "Flow.Spawn in this event handler runs when the event fires, where there may be no flow (FlowMisuseException) or a different one that ends with it; start the work with Run on a FlowWorld taken in this flow (not FlowWorld.Current, which is null outside a flow), or turn the event into a signal with FlowBridge.FromCallback and await it in this flow",
        "Flow.Spawn in this event handler",
        "Flow.Spawn in this event handler",
        "Flow.Spawn in this event handler",
        "Flow.Spawn in this event handler",
        "Flow.Spawn in this event handler");

    [Test]
    public Task FLOW009_SpawnInASynchronousCallRunNowIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask Do(int x) { await FlowTask.NextFrame(); }
            static async FlowTask OpenShop() { await FlowTask.NextFrame(); }

            async FlowTask Menu(Button button, List<int> items)
            {
                items.ForEach(x => {|FLOW008:Flow.Spawn(Do(x))|}); // runs now, in this flow: the handle is dropped (FLOW008)
                Action now = () => {|FLOW008:Flow.Spawn(OpenShop())|};
                now();

                // The attach callback runs now; the event handler it subscribes only emits.
                using var clicks = FlowBridge.FromCallback<int>(emit =>
                {
                    _ = Flow.Spawn(OpenShop());
                    Action h = () => emit(1);
                    button.Clicked += h;
                    return () => button.Clicked -= h;
                });

                Action combined = null;
                combined += () => {|FLOW008:Flow.Spawn(OpenShop())|}; // a delegate, not an event
                combined();
                await clicks.Next();
            }
        }
        """ + Button);

    [Test]
    public Task FLOW009_WorldRunInAnEventHandlerIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            static async FlowTask OpenShop() { await FlowTask.NextFrame(); }

            async FlowTask Menu(Button button, FlowWorld world)
            {
                button.Clicked += () => world.Run(OpenShop());
                button.Pressed += (sender, e) => world.Run(OpenShop());
                await FlowTask.NextFrame();
            }
        }
        """ + Button);
}
