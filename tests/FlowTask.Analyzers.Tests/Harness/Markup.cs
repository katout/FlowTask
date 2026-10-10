using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers.Tests.Harness;

/// <summary>An expected diagnostic: id and source span.</summary>
public readonly struct ExpectedDiagnostic : IEquatable<ExpectedDiagnostic>
{
    public ExpectedDiagnostic(string id, TextSpan span)
    {
        Id = id;
        Span = span;
    }

    public string Id { get; }
    public TextSpan Span { get; }

    public bool Equals(ExpectedDiagnostic other) => Id == other.Id && Span == other.Span;
    public override bool Equals(object obj) => obj is ExpectedDiagnostic other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, Span);
}

/// <summary>
/// Parses diagnostic markup: <c>{|FLOW003:text|}</c> marks the span of an expected diagnostic,
/// <c>{|FLOW003,FLOW008:text|}</c> several diagnostics on the same span. Markup may nest.
/// </summary>
public static class Markup
{
    public static string Parse(string markup, out List<ExpectedDiagnostic> expected)
    {
        expected = new List<ExpectedDiagnostic>();
        var output = new StringBuilder(markup.Length);
        var open = new Stack<(string[] Ids, int Start)>();
        var i = 0;
        while (i < markup.Length)
        {
            if (i + 1 < markup.Length && markup[i] == '{' && markup[i + 1] == '|')
            {
                var colon = markup.IndexOf(':', i + 2);
                if (colon < 0) throw new FormatException("Unterminated '{|ID:' markup at " + i);
                var ids = markup.Substring(i + 2, colon - i - 2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                open.Push((ids, output.Length));
                i = colon + 1;
                continue;
            }

            if (i + 1 < markup.Length && markup[i] == '|' && markup[i + 1] == '}' && open.Count > 0)
            {
                var (ids, start) = open.Pop();
                foreach (var id in ids) expected.Add(new ExpectedDiagnostic(id, TextSpan.FromBounds(start, output.Length)));
                i += 2;
                continue;
            }

            output.Append(markup[i]);
            i++;
        }

        if (open.Count > 0) throw new FormatException("Unclosed '{|' markup.");
        return output.ToString();
    }
}
