// The namespaces most Unity integration tests use; a file adds its own using directives for the others. Not System:
// with UnityEngine, Object and Random would be ambiguous in every file.
global using System.Collections;
global using System.Collections.Generic;
global using NUnit.Framework;
global using UnityEngine;
global using UnityEngine.TestTools;
global using static Katout.FlowTask.Unity.Tests.TestUtil;
