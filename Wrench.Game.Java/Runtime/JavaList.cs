using System.Collections;
using Wrench;

namespace Zomboid.Java;

/// <summary>A Java <c>List</c> (ArrayList and friends) with typed elements.</summary>
public sealed class JavaList<T> : JavaObject, IReadOnlyList<T>
{
    internal JavaList(GameObject handle) : base(handle)
    {
    }

    /// <summary>A new, empty <c>java.util.ArrayList</c>.</summary>
    public JavaList() : this(JavaNew("ArrayList"))
    {
    }

    /// <summary>A new, empty <c>java.util.ArrayList</c>, as Lua's <c>ArrayList.new()</c>.</summary>
    public static JavaList<T> New() => JavaStatic<JavaList<T>>("ArrayList", "new");

    public int Count => JavaCall<int>(this, "size");

    public T this[int index] => JavaCall<T>(this, "get", index);

    public void Add(T item) => JavaCallVoid(this, "add", item);

    public bool Contains(T item) => JavaCall<bool>(this, "contains", item);

    public IEnumerator<T> GetEnumerator()
    {
        var count = Count;
        for (var i = 0; i < count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
