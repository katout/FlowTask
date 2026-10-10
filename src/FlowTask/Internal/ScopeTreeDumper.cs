using System.Globalization;
using System.Text;

namespace Katout.FlowTask.Internal;

internal static class ScopeTreeDumper
{
    internal static string Dump(FlowWorld world)
    {
        var sb = new StringBuilder();
        // The paused clocks and who holds them, above the tree: a scope marked "paused" below waits for these.
        var header = false;
        foreach (var clock in world.Clocks)
        {
            var pause = clock.DescribePause();
            if (pause == null) continue;
            if (!header) sb.Append("Paused clocks:\n");
            header = true;
            sb.Append("  ").Append(clock.Name).Append(": ").Append(pause).Append('\n');
        }

        AppendTree(sb, world);
        return sb.ToString();
    }

    /// <summary>Tree levels drawn with guides; deeper nodes keep that indentation and show their depth.</summary>
    const int MaxDrawnLevels = 64;

    /// <summary>
    /// Every node in start order. Iterative over the tree links, since a chain of awaits can be thousands of nodes deep;
    /// past <see cref="MaxDrawnLevels"/> the indentation stops growing, so the text stays linear in the number of nodes.
    /// </summary>
    static void AppendTree(StringBuilder sb, FlowWorld world)
    {
        var root = world.Root;
        AppendLine(sb, world, root, true);
        var guides = new StringBuilder();
        var depth = 1;
        var n = root.FirstChild;
        while (n != null)
        {
            sb.Append(guides);
            if (depth > MaxDrawnLevels) sb.Append("[depth ").Append(depth).Append("] ");
            sb.Append(n.NextSibling == null ? "└─ " : "├─ ");
            AppendLine(sb, world, n, false);
            if (n.FirstChild != null)
            {
                if (depth < MaxDrawnLevels) guides.Append(n.NextSibling == null ? "   " : "│  ");
                depth++;
                n = n.FirstChild;
                continue;
            }

            while (n.NextSibling == null)
            {
                n = n.Parent;
                depth--;
                if (ReferenceEquals(n, root))
                {
                    n = null;
                    break;
                }

                if (depth < MaxDrawnLevels) guides.Length -= 3;
            }

            n = n?.NextSibling;
        }
    }

    static void AppendLine(StringBuilder sb, FlowWorld world, FlowNode n, bool isRoot)
    {
        sb.Append(n.DisplayName);
        if (!isRoot) sb.Append(" (").Append(KindText(new FlowScopeInfo(n).Kind)).Append(')');
        var clock = n.Clock;
        if (clock != null) sb.Append(" [").Append(clock.Name).Append(clock.LiveWorld == null ? ", removed" : clock.EffectivelyPaused ? ", paused" : "").Append(']');
        var wait = n.DescribeWait();
        if (wait != null)
        {
            sb.Append(" waiting: ").Append(wait);
            if (n.IsStateMachine) sb.Append(" for ").Append((world.UnscaledClock.Time - n.SuspendedAt).ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
        }

        if (n.IsCancelConfirmed) sb.Append(" <canceling: ").Append(n.Cause).Append('>');
        sb.Append('\n');
    }

    static string KindText(FlowScopeKind kind) => kind switch
    {
        FlowScopeKind.Scope => "scope",
        FlowScopeKind.Wait => "wait",
        FlowScopeKind.Combinator => "combinator",
        _ => "root",
    };
}
