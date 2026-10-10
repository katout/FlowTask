using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// The analyzers' strings in English (Resources.resx, the neutral language) and Japanese (Resources.ja.resx), looked up
/// in the culture the host asks for (docs/ja/tools/analyzers.md, 表示言語). RS1007 stops a descriptor built from a literal; these
/// tests check the result, the translation's coverage, and the text itself, which RS1032 / RS1033 do not see in a resx.
/// </summary>
public class LocalizationTests
{
    static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja");

    static readonly Regex KeyPattern = new(@"^(Flow\d{3}(Title|Description|Message\w*)|CodeFix\w+)$");

    static readonly Regex Placeholder = new(@"\{\d+\}");

    /// <summary>The strings that are formatted with arguments: diagnostic messages and code fix titles.</summary>
    static readonly Regex FormattedKey = new(@"^(Flow\d{3}Message|CodeFix)");

    /// <summary>Requirement ids ("XX-00") and member names (Flow.Spawn, world.Run) that a translation keeps as they are.</summary>
    static readonly Regex KeptVerbatim = new(@"\b[A-Z]{2}-\d{2}\b|\b[A-Za-z]\w*\.[A-Z]\w*");

    static readonly Regex JapaneseCharacter = new(@"[぀-ヿ一-鿿]");

    static IEnumerable<DiagnosticDescriptor> AllDescriptors => AnalyzerHarness.Analyzers.SelectMany(a => a.SupportedDiagnostics);

    static LocalizableResourceString Resource(string key) => new(key, Resources.ResourceManager, typeof(Resources));

    /// <summary>Key and text of every string in the resources of <paramref name="culture"/> itself (no fallback).</summary>
    static SortedDictionary<string, string> Strings(CultureInfo culture)
    {
        var set = Resources.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.That(set, Is.Not.Null, "no resources for '" + culture.Name + "'");
        var strings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in set) strings.Add((string)entry.Key, (string)entry.Value);
        return strings;
    }

    static string[] Matches(Regex regex, string text) =>
        regex.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Requirement ids ("XX-00") and execution-order rule numbers ("R" and a digit), which users cannot look up
    /// (AGENTS.md). R3 is left out: it is also the name of the R3 library, which FlowTask.R3
    /// bridges.
    /// </summary>
    static readonly Regex RequirementId = new Regex(@"(?<![A-Za-z0-9])([A-Z]{2}-\d{2}|R[124-9])(?![0-9A-Za-z])");

    [Test]
    public void NoStringCitesARequirementId()
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, Japanese })
        {
            foreach (var entry in Strings(culture))
            {
                Assert.That(Matches(RequirementId, entry.Value), Is.Empty,
                    $"{entry.Key} ({(culture.Name.Length == 0 ? "en" : culture.Name)}) cites an id users cannot look up; describe the behavior instead");
            }
        }
    }

    [Test]
    public void EveryDescriptorStringIsAResourceStringOfItsRule()
    {
        var keys = Strings(CultureInfo.InvariantCulture).Keys;
        foreach (var d in AllDescriptors)
        {
            var prefix = "Flow" + d.Id["FLOW".Length..];
            Assert.That(d.Title, Is.EqualTo(Resource(prefix + "Title")), d.Id);
            Assert.That(d.Description, Is.EqualTo(Resource(prefix + "Description")), d.Id);
            Assert.That(d.MessageFormat, Is.InstanceOf<LocalizableResourceString>(), d.Id);
            var messageKeys = keys.Where(k => k.StartsWith(prefix + "Message", StringComparison.Ordinal));
            Assert.That(messageKeys.Count(k => d.MessageFormat.Equals(Resource(k))), Is.EqualTo(1), d.Id + ": the message is not one of " + prefix + "Message*");
        }
    }

    [Test]
    public void EveryRuleStringIsUsedAndKeysFollowTheNaming()
    {
        var used = AllDescriptors.SelectMany(d => new[] { d.Title, d.MessageFormat, d.Description }).ToList();
        foreach (var key in Strings(CultureInfo.InvariantCulture).Keys)
        {
            Assert.That(KeyPattern.IsMatch(key), Is.True, key + ": FlowNNNTitle / FlowNNNDescription / FlowNNNMessage* / CodeFix*");
            if (key.StartsWith("Flow", StringComparison.Ordinal))
            {
                Assert.That(used.Any(s => s.Equals(Resource(key))), Is.True, key + " is not used by any descriptor");
            }
        }
    }

    /// <summary>
    /// A string added to Resources.resx needs its Japanese text too. The same text in both files is a string that was
    /// not translated (every string has words; identifiers alone are never a whole string).
    /// </summary>
    [Test]
    public void JapaneseResourcesCoverEveryNeutralKey()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        var japanese = Strings(Japanese);
        Assert.That(japanese.Keys, Is.EqualTo(english.Keys), "Resources.ja.resx and Resources.resx have different keys");
        foreach (var pair in japanese)
        {
            Assert.That(pair.Value, Is.Not.EqualTo(english[pair.Key]), pair.Key + " is not translated");
            Assert.That(JapaneseCharacter.IsMatch(pair.Value), Is.True, pair.Key + " has no Japanese text");
        }
    }

    /// <summary>
    /// The same {n} in both languages (a missing one drops the argument, an extra one throws when the message is
    /// formatted), and the same requirement ids and member names: most edits of the English text change one of them,
    /// so this catches a Japanese text left behind.
    /// </summary>
    [Test]
    public void PlaceholdersIdsAndMemberNamesMatchBetweenLanguages()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        foreach (var pair in Strings(Japanese))
        {
            Assert.That(Matches(Placeholder, pair.Value), Is.EqualTo(Matches(Placeholder, english[pair.Key])), pair.Key + ": placeholders");
            Assert.That(Matches(KeptVerbatim, pair.Value), Is.EqualTo(Matches(KeptVerbatim, english[pair.Key])), pair.Key + ": requirement ids and member names");
        }
    }

    /// <summary>
    /// Messages and code fix titles are composite formats. When string.Format throws (a stray brace), Roslyn shows the
    /// message as it is, {n} included, and the tests run in English, so a broken Japanese text would reach only Japanese
    /// users. Each one formats in both languages with as many arguments as the English text takes, and shows them all.
    /// </summary>
    [Test]
    public void MessagesAndCodeFixTitlesFormatInBothLanguages()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        var japanese = Strings(Japanese);
        foreach (var pair in english.Where(p => FormattedKey.IsMatch(p.Key)))
        {
            var count = Matches(Placeholder, pair.Value).Select(p => int.Parse(p.Trim('{', '}'), CultureInfo.InvariantCulture) + 1).DefaultIfEmpty(0).Max();
            var arguments = Enumerable.Range(0, count).Select(i => (object)("<argument " + i + ">")).ToArray();
            Assert.That(japanese.ContainsKey(pair.Key), Is.True, pair.Key + ": no Japanese text");
            foreach (var (language, text) in new[] { ("en", pair.Value), ("ja", japanese[pair.Key]) })
            {
                string formatted = null;
                Assert.That(() => formatted = string.Format(CultureInfo.InvariantCulture, text, arguments), Throws.Nothing, pair.Key + " (" + language + ")");
                foreach (var argument in arguments) Assert.That(formatted, Does.Contain((string)argument), pair.Key + " (" + language + ")");
            }
        }
    }

    [Test]
    public void TextFollowsThePunctuationRules()
    {
        AssertPunctuation(Strings(CultureInfo.InvariantCulture), ".");
        AssertPunctuation(Strings(Japanese), "。");
    }

    /// <summary>
    /// Descriptions end with a sentence end; titles, messages and code fix titles do not (the IDE and the compiler show
    /// them as they are, like the compiler's own messages). No surrounding white space or line break.
    /// </summary>
    static void AssertPunctuation(IDictionary<string, string> strings, string sentenceEnd)
    {
        foreach (var pair in strings)
        {
            Assert.That(pair.Value, Is.Not.Empty, pair.Key);
            Assert.That(pair.Value.Trim(), Is.EqualTo(pair.Value), pair.Key + ": white space at an end");
            Assert.That(pair.Value, Does.Not.Contain("\n"), pair.Key + ": line break");
            if (pair.Key.EndsWith("Description", StringComparison.Ordinal))
            {
                Assert.That(pair.Value, Does.EndWith(sentenceEnd), pair.Key);
            }
            else
            {
                Assert.That(pair.Value, Does.Not.EndWith(".").And.Not.EndWith("。"), pair.Key);
            }
        }
    }

    /// <summary>
    /// The Japanese title of each rule is its heading in docs/ja/tools/analyzers.md (without the severity), so a title
    /// found in the IDE can be searched in the document.
    /// </summary>
    [Test]
    public void JapaneseTitlesAreTheHeadingsOfAnalyzersMd()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FlowTask.slnx"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "repository root (FlowTask.slnx) not found above the test directory");

        var headings = Regex.Matches(File.ReadAllText(Path.Combine(directory.FullName, "docs", "ja", "tools", "analyzers.md")), @"^## (FLOW\d{3}): (.+)（[^（）]+）\r?$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        foreach (var id in AllDescriptors.Select(d => d.Id).Distinct())
        {
            Assert.That(headings.ContainsKey(id), Is.True, id + ": no '## " + id + ": ...（重大度）' heading");
            Assert.That(AllDescriptors.First(d => d.Id == id).Title.ToString(Japanese), Is.EqualTo(headings[id]), id);
        }
    }

    /// <summary>
    /// A diagnostic is formatted when the host asks for its text: Japanese for ja and ja-JP (which falls back to ja),
    /// English for en-US (Unity's compiler), the invariant culture and every other language (fr-FR has no resources).
    /// </summary>
    [Test]
    public async Task DiagnosticsAreFormattedInTheRequestedCulture()
    {
        var compilation = AnalyzerHarness.CreateCompilation("""
            using Katout.FlowTask;

            class C
            {
                static FlowTask Intro() => FlowTask.CompletedTask;

                async FlowTask M()
                {
                    var intro = Intro();
                    await FlowTask.NextFrame();
                }
            }
            """);
        AnalyzerHarness.AssertCompiles(compilation);
        var diagnostic = (await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation)).Single();
        Assert.That(diagnostic.Id, Is.EqualTo(DiagnosticIds.FlowTaskDropped));

        var english = "The FlowTask stored in 'intro' is never awaited or started, so it never runs";
        Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture), Is.EqualTo(english));
        Assert.That(diagnostic.GetMessage(CultureInfo.GetCultureInfo("en-US")), Is.EqualTo(english));
        Assert.That(diagnostic.GetMessage(CultureInfo.GetCultureInfo("fr-FR")), Is.EqualTo(english));

        var japanese = "'intro' に入れた FlowTask は await も開始もされないため、実行されません";
        Assert.That(diagnostic.GetMessage(Japanese), Is.EqualTo(japanese));
        Assert.That(diagnostic.GetMessage(CultureInfo.GetCultureInfo("ja-JP")), Is.EqualTo(japanese));
        Assert.That(diagnostic.Descriptor.Title.ToString(CultureInfo.GetCultureInfo("ja-JP")), Is.EqualTo("FlowTask は await するか開始する"));
        Assert.That(diagnostic.Descriptor.Description.ToString(CultureInfo.GetCultureInfo("en-US")), Does.StartWith("FlowTasks are lazy: calling a FlowTask method only creates the task"));
    }

    /// <summary>One source per code fix provider; together they offer every code fix title of the resources.</summary>
    static readonly (string Source, CodeFixProvider Provider, string DiagnosticId, int Index)[] CodeFixCases =
    {
        ("""
         using System;
         using Katout.FlowTask;
         class C
         {
             async FlowTask M()
             {
                 try { await FlowTask.NextFrame(); }
                 catch (OperationCanceledException) { return; }
             }
         }
         """, new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation, 0),
        ("""
         using System.Threading.Tasks;
         using Katout.FlowTask;
         class C
         {
             async FlowTask M()
             {
                 await Task.Delay(1);
             }
         }
         """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask, 0),
        ("""
         using Katout.FlowTask;
         class C
         {
             static FlowTask Intro() => FlowTask.CompletedTask;
             async FlowTask M()
             {
                 Intro();
                 await FlowTask.NextFrame();
             }
         }
         """, new StartFlowTaskCodeFixProvider(), DiagnosticIds.FlowTaskDropped, 0),
        ("""
         using Katout.FlowTask;
         class C
         {
             async FlowTask M(Clock clock)
             {
                 clock.Pause();
                 var paused = clock.Pause();
                 await FlowTask.NextFrame();
             }
         }
         """, new UsingLifetimeHandleCodeFixProvider(), DiagnosticIds.LifetimeHandleDropped, 0),
        ("""
         using Katout.FlowTask;
         class C
         {
             async FlowTask M(Clock clock)
             {
                 clock.Pause();
                 var paused = clock.Pause();
                 await FlowTask.NextFrame();
             }
         }
         """, new UsingLifetimeHandleCodeFixProvider(), DiagnosticIds.LifetimeHandleDropped, 1),
        ("""
         using Katout.FlowTask;
         class C
         {
             async FlowTask M(Signal<int> hits)
             {
                 while (true)
                 {
                     await hits.Next();
                     await FlowTask.NextFrame();
                 }
             }
         }
         """, new SubscribeBeforeLoopCodeFixProvider(), DiagnosticIds.SignalNextInLoop, 0),
        ("""
         using Katout.FlowTask;
         class C
         {
             static FlowTask Music() => FlowTask.CompletedTask;
             async FlowTask M()
             {
                 Flow.Spawn(Music());
                 await FlowTask.NextFrame();
             }
         }
         """, new SpawnHandleCodeFixProvider(), DiagnosticIds.SpawnHandleDropped, 0),
        ("""
         using Katout.FlowTask;
         class C
         {
             async FlowTask M()
             {
                 try { await FlowTask.NextFrame(); }
                 finally { await FlowTask.NextFrame(); }
             }
         }
         """, new NonCancelableCodeFixProvider(), DiagnosticIds.CleanupAwaitNotProtected, 0),
    };

    /// <summary>The key of the title each code fix of <see cref="CodeFixCases"/> should show, in order.</summary>
    static readonly (string Key, string Argument)[] ExpectedCodeFixTitles =
    {
        ("CodeFixRethrowCancellation", null),
        ("CodeFixRemoveCancellationCatch", null),
        ("CodeFixExcludeFlowCancellation", "e"),
        ("CodeFixBridgeWithFromTask", null),
        ("CodeFixBridgeWithAsFlow", null),
        ("CodeFixAwait", null),
        ("CodeFixSpawn", null),
        ("CodeFixRun", null),
        ("CodeFixUsingVar", "pause"),
        ("CodeFixUsingDeclaration", "paused"),
        ("CodeFixSubscribeBeforeLoop", null),
        ("CodeFixDiscardSpawnHandle", null),
        ("CodeFixRunInsteadOfSpawn", null),
        ("CodeFixNonCancelable", null),
    };

    /// <summary>
    /// RS1007 sees descriptors only, so code fix titles stay in the resources through the next test alone: every code
    /// fix provider, a new one included, needs a case there (like the analyzers AnalyzerHarness collects).
    /// </summary>
    [Test]
    public void EveryCodeFixProviderHasATitleCase()
    {
        var providers = typeof(StartFlowTaskCodeFixProvider).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();
        Assert.That(providers, Is.Not.Empty);
        Assert.That(CodeFixCases.Select(c => c.Provider.GetType().FullName).Distinct(), Is.EquivalentTo(providers),
            "each code fix provider needs a CodeFixCases entry, and its titles in ExpectedCodeFixTitles");
    }

    /// <summary>
    /// Code fix titles are read when the IDE asks for the fixes, in its UI culture. The equivalence keys (Fix All) do
    /// not depend on the language.
    /// </summary>
    [TestCase("en-US")]
    [TestCase("ja-JP")]
    public async Task CodeFixTitlesFollowTheUICulture(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var saved = CultureInfo.CurrentUICulture;
        var titles = new List<string>();
        var equivalenceKeys = new List<string>();
        CultureInfo.CurrentUICulture = culture;
        try
        {
            foreach (var (source, provider, id, index) in CodeFixCases)
            {
                foreach (var action in await CodeFixHarness.GetActionsAsync(source, provider, id, index))
                {
                    titles.Add(action.Title);
                    equivalenceKeys.Add(action.EquivalenceKey);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }

        var expected = ExpectedCodeFixTitles
            .Select(t => string.Format(CultureInfo.InvariantCulture, Resources.ResourceManager.GetString(t.Key, culture), t.Argument))
            .ToArray();
        Assert.That(titles, Is.EqualTo(expected));
        Assert.That(Strings(CultureInfo.InvariantCulture).Keys.Where(k => k.StartsWith("CodeFix", StringComparison.Ordinal)),
            Is.EquivalentTo(ExpectedCodeFixTitles.Select(t => t.Key)), "a CodeFix* resource that no case shows");
        if (culture.TwoLetterISOLanguageName == "ja") Assert.That(titles.All(t => JapaneseCharacter.IsMatch(t)), Is.True, string.Join(" / ", titles));
        Assert.That(equivalenceKeys, Is.EqualTo(new[]
        {
            CatchCancellationCodeFixProvider.RethrowEquivalenceKey,
            CatchCancellationCodeFixProvider.RemoveEquivalenceKey,
            CatchCancellationCodeFixProvider.ExcludeEquivalenceKey,
            BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey,
            BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey,
            StartFlowTaskCodeFixProvider.AwaitEquivalenceKey,
            StartFlowTaskCodeFixProvider.SpawnEquivalenceKey,
            StartFlowTaskCodeFixProvider.RunEquivalenceKey,
            nameof(UsingLifetimeHandleCodeFixProvider),
            UsingLifetimeHandleCodeFixProvider.UsingDeclarationEquivalenceKey,
            SubscribeBeforeLoopCodeFixProvider.LatestEquivalenceKey,
            SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
            SpawnHandleCodeFixProvider.RunEquivalenceKey,
            nameof(NonCancelableCodeFixProvider),
        }));
    }
}
