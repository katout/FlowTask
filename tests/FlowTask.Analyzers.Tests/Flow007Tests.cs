namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW007 (Warning): GetAwaiter() of a FlowTask awaitable called in code starts the task.</summary>
public class Flow007Tests
{
    [Test]
    public Task FLOW007_GetAwaiterOfAFlowTaskAwaitableCalledInCodeIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using Katout.FlowTask;

        class Menu
        {
            static async FlowTask LoadIcons() { await FlowTask.NextFrame(); }
            static async FlowTask<int> Count() { await FlowTask.NextFrame(); return 1; }

            async FlowTask Peek(Once<int> ready, Once<int> maybe, Menu other)
            {
                var icons = {|FLOW007:LoadIcons().GetAwaiter()|};
                if ({|FLOW007:Count().GetAwaiter()|}.IsCompleted) { }
                var value = {|FLOW007:ready.GetAwaiter()|};
                var conditional = maybe?{|FLOW007:.GetAwaiter()|};
                var extension = {|FLOW007:MenuExtensions.GetAwaiter(other)|};
                await FlowTask.NextFrame();
            }

            void OutsideAFlow()
            {
                {|FLOW007:FlowTask.WaitForSeconds(1).GetAwaiter()|}.OnCompleted(() => { });
            }
        }

        static class MenuExtensions
        {
            public static FlowTask.Awaiter GetAwaiter(this Menu menu) => FlowTask.NextFrame().GetAwaiter();
        }
        """,
        "'LoadIcons().GetAwaiter()' starts the task here, because a FlowTask starts in GetAwaiter when it is awaited; await it instead, or start it with Flow.Spawn or FlowWorld.Run",
        "'Count().GetAwaiter()'",
        "'ready.GetAwaiter()'",
        "'maybe.GetAwaiter()'",
        "'other.GetAwaiter()'",
        "'FlowTask.WaitForSeconds(1).GetAwaiter()'");

    [Test]
    public Task FLOW007_AwaitAndAGetAwaiterMethodThatHandsOutTheAwaiterAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class Door
        {
            public bool Open;
            public FlowTask Opened() => FlowTask.WaitUntil(() => Open);

            // An awaitable of the user's own: its await calls this, which hands out the FlowTask's awaiter.
            public FlowTask.Awaiter GetAwaiter() => Opened().GetAwaiter();
        }

        static class DoorExtensions
        {
            public static FlowTask.Awaiter GetAwaiter(this (Door a, Door b) doors) => doors.a.Opened().GetAwaiter();
        }

        class Game
        {
            static async FlowTask Enter(Door door, Once<int> key)
            {
                await door;
                await (door, door);
                await door.Opened();
                var k = await key;
                await Task.Delay(1).AsFlow();
            }

            static void NotAFlowTaskAwaiter()
            {
                Task.Delay(1).GetAwaiter().GetResult();
            }
        }
        """);
}
