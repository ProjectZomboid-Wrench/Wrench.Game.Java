using Wrench;

namespace Zomboid;

public static class GameObjectExtensions
{
    /// <summary>Views a Lua value as a typed game object, e.g. an event argument: <c>args[0].As&lt;IsoPlayer&gt;()</c>. Unchecked.</summary>
    public static T As<T>(this GameObject obj) where T : JavaObject => JavaTypes.Create<T>(obj);
}
