using Wrench;

namespace Zomboid.Java;

/// <summary>
/// Base of the generated wrappers for the Java classes PZ exposes to Lua. A wrapper is a typed view of a
/// <see cref="GameObject"/>, so it has the same lifetime: valid during the handler or call that produced it.
/// </summary>
public abstract class JavaObject : IEquatable<JavaObject>, IGameObjectView
{
    private readonly GameObject _ref;

    protected JavaObject(GameObject handle) => _ref = handle ?? throw new ArgumentNullException(nameof(handle));

    /// <summary>The underlying object, for members the generated API doesn't cover (use it as <c>dynamic</c>).</summary>
    public GameObject AsGameObject() => _ref;

    GameObject IGameObjectView.View => Ref;

    private GameObject Ref => _ref.IsValid ? _ref : throw _ref.Expired(GetType().Name);

    /// <summary>Views this object as another wrapper type, e.g. an interface it implements. Unchecked.</summary>
    public T As<T>() where T : JavaObject => JavaTypes.Create<T>(_ref);

    public bool Equals(JavaObject? other) => other is not null && _ref == other._ref;

    public override bool Equals(object? obj) => obj is JavaObject other && Equals(other);

    public override int GetHashCode() => _ref.GetHashCode();

    public override string ToString() => _ref.ToString();

    public static bool operator ==(JavaObject? a, JavaObject? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(JavaObject? a, JavaObject? b) => !(a == b);

    protected static T JavaCall<T>(JavaObject self, string method, params object?[] args) =>
        JavaConvert.To<T>(First(LuaHost.Required.CallMethod(null, self.Ref, method, Unwrap(args))));

    protected static void JavaCallVoid(JavaObject self, string method, params object?[] args) =>
        LuaHost.Required.CallMethod(null, self.Ref, method, Unwrap(args));

    protected static T JavaStatic<T>(string table, string function, params object?[] args) =>
        JavaConvert.To<T>(First(LuaHost.Required.CallStatic(null, table, function, Unwrap(args))));

    protected static void JavaStaticVoid(string table, string function, params object?[] args) =>
        LuaHost.Required.CallStatic(null, table, function, Unwrap(args));

    /// <summary>A new Java object, as Lua's <c>Class.new(...)</c>; for the generated constructors.</summary>
    protected static GameObject JavaNew(string table, params object?[] args) =>
        First(LuaHost.Required.CallStatic(null, table, "new", Unwrap(args))) as GameObject
        ?? throw new LuaException($"{table}.new returned nothing");

    protected static T JavaField<T>(string table, string field) => JavaConvert.To<T>(LuaHost.Required.GetStatic(table, field));

    /// <summary>Whole numbers as text, so formatting functions such as getText print 3 rather than 3.0.</summary>
    internal static object[] TextArguments(object[] args) =>
        [.. args.Select(a => a is int or long or short or byte or sbyte or uint or ulong or ushort
            ? Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)!
            : a)];

    internal static T CallGlobal<T>(string function, object?[] args) =>
        JavaConvert.To<T>(First(LuaHost.Required.CallGlobal(null, function, Unwrap(args))));

    internal static void CallGlobalVoid(string function, object?[] args) =>
        LuaHost.Required.CallGlobal(null, function, Unwrap(args));

    private static object? First(object?[] results) => results.Length > 0 ? results[0] : null;

    private static object?[] Unwrap(object?[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is JavaObject wrapper)
                args[i] = wrapper.Ref;
        }
        return args;
    }
}
