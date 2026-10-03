namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill. Records with <c>init</c> accessors need this type, and netstandard2.0 — which Roslyn
/// analyzers must target, since the compiler loads them into its own process — does not ship it.
/// </summary>
internal static class IsExternalInit;
