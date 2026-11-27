using System.Collections.Concurrent;
using System.Globalization;
using Wrench;

namespace Zomboid.Java;

/// <summary>Converts what Kahlua hands back (Lua numbers are doubles, objects are GameObjects) to the declared type.</summary>
internal static class JavaConvert
{
    private static readonly ConcurrentDictionary<Type, Func<GameObject, object>> ListFactories = new();

    public static T To<T>(object? value)
    {
        if (value is T typed)
            return typed;
        if (value is double number && TryNumber(number, out T converted))
            return converted;
        return (T)To(value, typeof(T))!;
    }

    // Lua numbers to C# number types without boxing, rounding as Convert (and so IConvertible.ToType) does.
    internal static bool TryNumber<T>(double number, out T result)
    {
        if (typeof(T) == typeof(float))
            result = (T)(object)Convert.ToSingle(number);
        else if (typeof(T) == typeof(int))
            result = (T)(object)Convert.ToInt32(number);
        else if (typeof(T) == typeof(long))
            result = (T)(object)Convert.ToInt64(number);
        else if (typeof(T) == typeof(short))
            result = (T)(object)Convert.ToInt16(number);
        else if (typeof(T) == typeof(byte))
            result = (T)(object)Convert.ToByte(number);
        else
        {
            result = default!;
            return false;
        }
        return true;
    }

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
