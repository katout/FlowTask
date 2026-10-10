// The namespaces most test files use. Unity (tools/unity/run-tests.ps1 copies it) and tests/godot compile this file with
// the tests, so it applies to the other files of those assemblies too: System.IO and System.Threading stay per file,
// because Godot has a FileAccess and a Timer of its own.
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
global using NUnit.Framework;
global using Katout.FlowTask.Diagnostics;
