using Wrench;

namespace Zomboid;

/// <summary>Picks the wrapper for a Java object: its exact class if exposed, otherwise the type asked for.</summary>
internal static partial class JavaTypes
{
    private static readonly Dictionary<string, Func<GameObject, JavaObject>> ByJavaName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, Func<GameObject, JavaObject>> ByType = new();

    static JavaTypes() => Register(ByJavaName, ByType);

    static partial void Register(Dictionary<string, Func<GameObject, JavaObject>> byJavaName,
                                 Dictionary<Type, Func<GameObject, JavaObject>> byType);

    public static T Create<T>(GameObject handle) where T : JavaObject => (T)Create(typeof(T), handle);

    public static JavaObject Create(Type type, GameObject handle)
    {
        if (ByJavaName.TryGetValue(handle.TypeName, out var exact))
        {
            var wrapper = exact(handle);
            if (type.IsInstanceOfType(wrapper))
                return wrapper;
        }
        return ByType.TryGetValue(type, out var declared)
            ? declared(handle)
            : throw new InvalidCastException($"No wrapper for {type.Name}");
    }
}
