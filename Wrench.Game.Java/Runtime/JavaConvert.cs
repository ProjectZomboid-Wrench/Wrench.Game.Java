using System.Collections.Concurrent;
using System.Globalization;
using Wrench;

namespace Zomboid.Java;

/// <summary>Converts what Kahlua hands back (Lua numbers are doubles, objects are GameObjects) to the declared type.</summary>
internal static class JavaConvert
{
    private static readonly ConcurrentDictionary<Type, Func<GameObject, object>> ListFactories = new();

    public static T To<T>(object? value) => (T)To(value, typeof(T))!;

    public static object? To(object? value, Type type)
    {
        if (value is null)
            return type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
        if (type.IsInstanceOfType(value))
            return value;
        if (value is GameObject handle)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(JavaList<>))
                return ListFactories.GetOrAdd(type, CreateListFactory)(handle);
            if (typeof(JavaObject).IsAssignableFrom(type))
                return JavaTypes.Create(type, handle);
        }
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string))
            return value.ToString();
        if (value is IConvertible convertible && typeof(IConvertible).IsAssignableFrom(target))
            return convertible.ToType(target, CultureInfo.InvariantCulture);
        throw new InvalidCastException($"Can't convert {value.GetType().Name} from Lua to {type.Name}");
    }

    private static Func<GameObject, object> CreateListFactory(Type listType)
    {
        var constructor = listType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return handle => constructor.Invoke([handle]);
    }
}
