// The namespaces every code fix uses; a file adds its own using directives for the others.
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Composition;
global using System.Threading;
global using System.Threading.Tasks;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CodeActions;
global using Microsoft.CodeAnalysis.CodeFixes;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.CodeAnalysis.CSharp.Syntax;
global using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
