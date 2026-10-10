#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets the compiler emit the init accessors of positional records on netstandard2.1 and in Unity, whose class
/// libraries do not have this type.
/// </summary>
internal static class IsExternalInit
{
}
#endif
