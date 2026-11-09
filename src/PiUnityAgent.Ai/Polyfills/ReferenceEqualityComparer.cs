using System.Runtime.CompilerServices;

#if NETSTANDARD2_1
namespace System.Collections.Generic;

/// <summary>
/// netstandard2.1 adaptation: the BCL <c>ReferenceEqualityComparer</c> is .NET 5+, and the one shipped
/// internally by System.Text.Json is inaccessible. Mirrors the BCL semantics (reference identity).
/// </summary>
internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    private ReferenceEqualityComparer()
    {
    }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
#endif
