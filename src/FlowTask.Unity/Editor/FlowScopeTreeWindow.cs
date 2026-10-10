using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Katout.FlowTask.Diagnostics;
using UnityEditor;
using UnityEngine;

// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace.
namespace Katout.FlowTask.Unity.Editor
{
    /// <summary>
    /// Live view of a World's scope tree (default: <see cref="FlowTaskUnity.World"/>). Shows every node with
    /// its kind, clock, what it waits for and for how long, and scopes that are canceling. Refreshes during Play Mode.
    /// Each node shows where what it waits for was created (<see cref="FlowScopeInfo.WaitingFile"/>,
    /// <see cref="FlowScopeInfo.WaitingLine"/>). Double-clicking a node opens that line; for a scope that waits on the call
    /// of another FlowTask method (no place), it opens the scope's method in its script
    /// (<see cref="FlowScopeInfo.DeclaringType"/>, <see cref="FlowScopeInfo.MethodName"/>). The context menu offers both.
    /// </summary>
    public sealed class FlowScopeTreeWindow : EditorWindow
    {
        [SerializeField] bool _autoRefresh = true;
        [SerializeField] bool _textView;
        [SerializeField] bool _scopesOnly;
        [SerializeField] float _interval = 0.25f;
        [SerializeField] string _filter = "";
        [SerializeField] int _worldIndex;

        readonly HashSet<string> _collapsed = new();
        Vector2 _scroll;
        double _nextRepaint;
        // A monospaced font of the OS (the first one installed), created by this window and destroyed with it.
        static readonly string[] s_monospacedFonts = { "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New" };
        Font _monoFont;
        GUIStyle _mono;
        GUIStyle _row;

        [MenuItem("Window/FlowTask/Scope Tree")]
        public static void Open()
        {
            var window = GetWindow<FlowScopeTreeWindow>();
            window.titleContent = new GUIContent("FlowTask Scopes");
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (_monoFont != null) DestroyImmediate(_monoFont);
            _monoFont = null;
            _mono = null;
        }

        void OnPlayModeChanged(PlayModeStateChange change) => Repaint();

        void OnEditorUpdate()
        {
            if (!_autoRefresh || !EditorApplication.isPlaying || EditorApplication.isPaused) return;
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + Mathf.Max(0.05f, _interval);
            Repaint();
        }

        void OnGUI()
        {
            if (_mono == null)
            {
                _monoFont = Font.CreateDynamicFontFromOSFont(s_monospacedFonts, 12);
                _monoFont.hideFlags = HideFlags.HideAndDontSave;
                _mono = new GUIStyle(EditorStyles.textArea) { font = _monoFont, wordWrap = false, richText = false };
                _row = new GUIStyle(EditorStyles.label) { richText = true };
            }

            var worlds = FlowWorldRegistry.Worlds;
            DrawToolbar(worlds);
            if (worlds.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    EditorApplication.isPlaying
                        ? "No FlowTask World is running. " + FlowTaskUnity.NoWorldReason()
                        : "Enter Play Mode to inspect the default FlowTask World (FlowTaskUnity.World). Other Worlds appear here after FlowWorldRegistry.Register(world).",
                    MessageType.Info);
                return;
            }

            _worldIndex = Mathf.Clamp(_worldIndex, 0, worlds.Count - 1);
            var world = worlds[_worldIndex];
            var d = world.Diagnostics;
            var clocks = string.Join("  ", world.Clocks.Select(c =>
                c.Name + "=" + c.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s" + (c.IsPausedInHierarchy ? " (paused)" : "") +
                (c.TimeScale != 1 ? " x" + c.TimeScale.ToString("0.##", CultureInfo.InvariantCulture) : "")));
            EditorGUILayout.LabelField(
                $"{world}   live scopes: {d.Walk().Count(s => s.Kind == FlowScopeKind.Scope)}",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(clocks, EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_textView)
            {
                EditorGUILayout.TextArea(world.Dump(), _mono, GUILayout.ExpandHeight(true));
            }
            else if (!string.IsNullOrEmpty(_filter))
            {
                foreach (var info in d.Walk())
                {
                    if (_scopesOnly && info.Kind != FlowScopeKind.Scope) continue;
                    if (!Matches(info, _filter)) continue;
                    var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                    EditorGUI.LabelField(rect, new GUIContent(Describe(info, withPath: true), SourceTooltip(info)), _row);
                    HandleSourceClick(rect, info);
                }
            }
            else
            {
                DrawNode(d.Root, 0, "0");
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawToolbar(IReadOnlyList<FlowWorld> worlds)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (worlds.Count > 1)
                {
                    var names = worlds.Select((w, i) => i + ": " + w.Name).ToArray();
                    _worldIndex = EditorGUILayout.Popup(_worldIndex, names, EditorStyles.toolbarPopup, GUILayout.Width(140));
                }

                _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto refresh", EditorStyles.toolbarButton);
                _textView = GUILayout.Toggle(_textView, "Text", EditorStyles.toolbarButton);
                _scopesOnly = GUILayout.Toggle(_scopesOnly, "Scopes only", EditorStyles.toolbarButton);
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) Repaint();
                GUILayout.FlexibleSpace();
                _filter = GUILayout.TextField(_filter ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(160));
                using (new EditorGUI.DisabledScope(worlds.Count == 0))
                {
                    if (GUILayout.Button("Copy dump", EditorStyles.toolbarButton) && worlds.Count > 0)
                        EditorGUIUtility.systemCopyBuffer = worlds[Mathf.Clamp(_worldIndex, 0, worlds.Count - 1)].Dump();
                }
            }
        }

        void DrawNode(FlowScopeInfo info, int depth, string key)
        {
            if (!info.IsValid) return;
            var children = info.Children;
            var visible = !_scopesOnly || depth == 0 || info.Kind == FlowScopeKind.Scope;
            var childDepth = depth;
            if (visible)
            {
                var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                rect.xMin += depth * 14f;
                var hasChildren = children.Count > 0;
                var expanded = !_collapsed.Contains(key);
                if (hasChildren)
                {
                    var foldRect = new Rect(rect.x, rect.y, 14f, rect.height);
                    var now = EditorGUI.Foldout(foldRect, expanded, GUIContent.none, true);
                    if (now != expanded)
                    {
                        if (now) _collapsed.Remove(key);
                        else _collapsed.Add(key);
                    }

                    expanded = now;
                }

                rect.xMin += 14f;
                EditorGUI.LabelField(rect, new GUIContent(Describe(info, withPath: false), SourceTooltip(info)), _row);
                HandleSourceClick(rect, info);
                if (!expanded) return;
                childDepth = depth + 1;
            }

            for (var i = 0; i < children.Count; i++) DrawNode(children[i], childDepth, key + "/" + i);
        }

        static string KindText(FlowScopeKind kind) => kind switch
        {
            FlowScopeKind.Root => "root",
            FlowScopeKind.Scope => "scope",
            FlowScopeKind.Wait => "wait",
            FlowScopeKind.Combinator => "combinator",
            _ => "",
        };

        static string Describe(FlowScopeInfo info, bool withPath)
        {
            var name = withPath ? info.Path ?? info.Name : info.Name;
            var text = info.Kind == FlowScopeKind.Root ? "<b>" + name + "</b>" : "<b>" + name + "</b> <color=#888888>(" + KindText(info.Kind) + ")</color>";
            if (info.ClockName != null) text += " [" + info.ClockName + "]";
            if (info.Status != FlowStatus.Running && info.Kind != FlowScopeKind.Root) text += " " + info.Status;
            var waiting = info.Waiting;
            if (waiting != null)
            {
                text += "  waiting: " + waiting;
                var site = SiteLabel(info.WaitingFile, info.WaitingLine);
                if (site != null) text += " <color=#888888>at " + site + "</color>";
                // Scopes, and waits and combinators run at the root with no scope under them (FlowScopeInfo.WaitingSeconds).
                var seconds = info.WaitingSeconds;
                if (info.Kind == FlowScopeKind.Scope || seconds > 0) text += " for " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }

            if (info.IsCanceling) text += "  <color=#ff9900>canceling (" + info.Cause + ")</color>";
            return text;
        }

        static bool Matches(FlowScopeInfo info, string filter) =>
            (info.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (info.Waiting?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        // ------------------------------------------------------------------ opening the source

        static string SourceTooltip(FlowScopeInfo info)
        {
            var site = SiteLabel(info.WaitingFile, info.WaitingLine);
            if (site != null) return "Double-click to open " + site;
            return info.DeclaringType == null ? null : "Double-click to open " + SourceLabel(info.DeclaringType, info.MethodName);
        }

        void HandleSourceClick(Rect rect, FlowScopeInfo info)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;
            // The node may be reused once its flow ends: the menu keeps the values, not the node.
            var type = info.DeclaringType;
            var method = info.MethodName;
            var file = info.WaitingFile;
            var line = info.WaitingLine;
            if (type == null && file == null) return;
            if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2)
            {
                if (file != null) OpenWaitSite(file, line);
                else OpenSource(type, method);
                e.Use();
            }
            else if (e.type == EventType.ContextClick)
            {
                var menu = new GenericMenu();
                if (file != null) menu.AddItem(new GUIContent("Open wait at " + SiteLabel(file, line)), false, () => OpenWaitSite(file, line));
                if (type != null) menu.AddItem(new GUIContent("Open " + SourceLabel(type, method)), false, () => OpenSource(type, method));
                menu.ShowAsContext();
                e.Use();
            }
        }

        /// <summary>"File.cs:42": the file name of a wait's place and its line, or null when the place is unknown.</summary>
        static string SiteLabel(string file, int line)
        {
            if (file == null) return null;
            var name = file[(file.LastIndexOfAny(s_separators) + 1)..];
            return line > 0 ? name + ":" + line.ToString(CultureInfo.InvariantCulture) : name;
        }

        static readonly char[] s_separators = { '/', '\\' };

        void OpenWaitSite(string file, int line)
        {
            var script = FindWaitScript(file);
            if (script != null)
            {
                AssetDatabase.OpenAsset(script, line > 0 ? line : -1);
                return;
            }

            // A file the asset database does not know (outside the project and its packages): the external editor opens it.
            if (!File.Exists(file) || !UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(file, line > 0 ? line : -1))
                ShowNotification(new GUIContent("No script found at " + file));
        }

        /// <summary>
        /// The script at <paramref name="file"/>, a path as <c>[CallerFilePath]</c> gives it: inside the project
        /// ("Assets/..."), or inside a package, whose files the asset database knows by "Packages/&lt;name&gt;/...". Null
        /// when it knows none there.
        /// </summary>
        internal static MonoScript FindWaitScript(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var path = file.Replace('\\', '/');
            var project = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');
            if (project != null && path.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase)) path = path[(project.Length + 1)..];
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (script != null) return script;
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                var root = package.resolvedPath?.Replace('\\', '/');
                if (string.IsNullOrEmpty(root) || !path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) continue;
                script = AssetDatabase.LoadAssetAtPath<MonoScript>(package.assetPath + path[root.Length..]);
                if (script != null) return script;
            }

            return null;
        }

        void OpenSource(Type type, string method)
        {
            var script = FindScript(type);
            if (script == null)
            {
                ShowNotification(new GUIContent("No script found for " + SourceLabel(type, method)));
                return;
            }

            var line = FindMethodLine(script.text, method);
            AssetDatabase.OpenAsset(script, line > 0 ? line : -1);
        }

        static string SourceLabel(Type type, string method) => method == null ? TypeName(type) : TypeName(type) + "." + method;

        /// <summary>The name of <paramref name="type"/> as written in the source: without the generic arity.</summary>
        static string TypeName(Type type)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name[..tick];
        }

        /// <summary>
        /// The script that declares <paramref name="type"/> (a generic type by its definition; a nested type, when no script
        /// is its own, by the type it is nested in): a script whose class it is, then one named after it that declares it,
        /// then any that declares it. Null when none does (a type from a precompiled assembly).
        /// </summary>
        internal static MonoScript FindScript(Type type)
        {
            if (type == null) return null;
            if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
            MonoScript[] runtimeScripts = null;
            MonoScript[] allScripts = null;
            for (var t = type; t != null; t = t.DeclaringType)
            {
                runtimeScripts ??= MonoImporter.GetAllRuntimeMonoScripts();
                foreach (var s in runtimeScripts)
                    if (s != null && s.GetClass() == t) return s;

                // Editor scripts are not in the runtime list.
                allScripts ??= AssetDatabase.FindAssets("t:MonoScript")
                    .Select(g => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(s => s != null)
                    .ToArray();
                foreach (var s in allScripts)
                    if (s.GetClass() == t) return s;

                var name = TypeName(t);
                MonoScript declaring = null;
                foreach (var s in allScripts)
                {
                    if (!Declares(s.text, t, name)) continue;
                    if (s.name == name) return s;
                    declaring ??= s;
                }

                if (declaring != null) return declaring;
            }

            return null;
        }

        /// <summary>Whether <paramref name="text"/> declares a type named <paramref name="name"/> in the namespace of <paramref name="type"/>.</summary>
        static bool Declares(string text, Type type, string name)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(name, StringComparison.Ordinal) < 0) return false;
            if (!string.IsNullOrEmpty(type.Namespace) && !Regex.IsMatch(text, @"\bnamespace\s+" + Regex.Escape(type.Namespace) + @"\s*[{;]")) return false;
            return Regex.IsMatch(text, @"\b(class|struct|record|interface)\s+" + Regex.Escape(name) + @"\b");
        }

        /// <summary>Words that come before a method name in a call or an expression, never as its return type.</summary>
        static readonly HashSet<string> s_notAReturnType = new()
        {
            "await", "return", "new", "throw", "else", "yield", "in", "is", "as", "case", "nameof", "typeof", "when", "using", "lock",
        };

        /// <summary>
        /// The 1-based line that declares <paramref name="method"/> in <paramref name="text"/>, or 0. An async method or local
        /// function (the scope's own method) is looked for first; otherwise a method of that name, which is where a lambda's
        /// containing method (<see cref="FlowScopeInfo.MethodName"/>) is declared. Comments are skipped; the first overload wins.
        /// </summary>
        internal static int FindMethodLine(string text, string method)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(method)) return 0;
            var name = Regex.Escape(method);
            var asyncDeclaration = new Regex(@"\basync\b[^=;{}]*\b" + name + @"\s*(<[^()]*>)?\s*\(");
            var declaration = new Regex(@"(?<type>[A-Za-z_][\w\[\]<>,?.]*)\s+" + name + @"\s*(<[^()]*>)?\s*\(");
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && asyncDeclaration.IsMatch(lines[i])) return i + 1;

            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                foreach (Match m in declaration.Matches(lines[i]))
                    if (!s_notAReturnType.Contains(m.Groups["type"].Value)) return i + 1;
            }

            return 0;
        }

        static bool IsComment(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal);
        }
    }
}
