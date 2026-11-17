import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Type;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.TreeMap;
import se.krka.kahlua.integration.annotations.LuaMethod;
import zombie.Lua.LuaManager;

/**
 * Dumps what PZ exposes to Lua, as Kahlua's LuaJavaClassExposer sees it (public methods via getMethods, constructors,
 * public static fields), for tools/GenerateLuaApi.cs. The class list comes from the Exposer's bytecode.
 * Varargs parameters end in "...". Tab-separated lines: C class nearestExposedSuperclass flags simpleName | M static name declaringClass return params... | K params... |
 * F name type | G luaName return params...
 */
public final class LuaApiDump {
    private static final TreeMap<String, Class<?>> sorted = new TreeMap<>();

    public static void main(String[] args) throws Exception {
        var loader = LuaApiDump.class.getClassLoader();
        for (var name : Files.readAllLines(Path.of(args[0]))) {
            if (name.isBlank())
                continue;
            try {
                sorted.put(name.trim(), Class.forName(name.trim(), false, loader));
            } catch (Throwable e) {
                System.err.println("can't load " + name + ": " + e);
            }
        }
        for (var type : sorted.values())
            dumpClass(type);
        dumpGlobals();
    }

    static void dumpClass(Class<?> type) {
        String flags = (type.isEnum() ? "enum " : "") + (type.isInterface() ? "interface " : "")
                + (Modifier.isAbstract(type.getModifiers()) ? "abstract" : "");
        // Unexposed classes in between (IsoPlayer extends IsoLivingCharacter extends IsoGameCharacter) are skipped.
        var superclass = type.getSuperclass();
        while (superclass != null && !sorted.containsKey(superclass.getName()))
            superclass = superclass.getSuperclass();
        print("C", type.getName(), superclass == null ? "-" : superclass.getName(), flags.trim(), type.getSimpleName());
        Method[] methods;
        try {
            methods = type.getMethods();
        } catch (Throwable e) {
            System.err.println("skipping methods of " + type.getName() + ": " + e);
            return;
        }
        for (var method : methods) {
            if (method.isBridge() || method.isSynthetic() || method.getDeclaringClass() == Object.class)
                continue;
            var line = new StringBuilder();
            line.append(Modifier.isStatic(method.getModifiers()) ? "1" : "0").append('\t').append(method.getName())
                    .append('\t').append(method.getDeclaringClass().getName())
                    .append('\t').append(name(method.getGenericReturnType()));
            line.append(parameters(method.getGenericParameterTypes(), method.isVarArgs()));
            print("M", line.toString());
        }
        if (!Modifier.isAbstract(type.getModifiers()) && !type.isInterface()) {
            for (Constructor<?> constructor : type.getConstructors())
                print("K", parameters(constructor.getGenericParameterTypes(), constructor.isVarArgs()).substring(Math.min(1, constructor.getParameterCount())));
        }
        for (Field field : type.getFields()) {
            if (Modifier.isStatic(field.getModifiers()) && field.getDeclaringClass() == type)
                print("F", field.getName(), name(field.getGenericType()));
        }
    }

    static void dumpGlobals() {
        for (var method : LuaManager.GlobalObject.class.getMethods()) {
            var annotation = method.getAnnotation(LuaMethod.class);
            if (annotation == null || !annotation.global())
                continue;
            var line = new StringBuilder(annotation.name().isEmpty() ? method.getName() : annotation.name());
            line.append('\t').append(name(method.getGenericReturnType()));
            line.append(parameters(method.getGenericParameterTypes(), method.isVarArgs()));
            print("G", line.toString());
        }
    }

    // Tab-prefixed parameter types; a varargs parameter ends in "..." instead of "[]".
    static String parameters(Type[] types, boolean varArgs) {
        var line = new StringBuilder();
        for (var i = 0; i < types.length; i++) {
            var text = name(types[i]);
            line.append('\t').append(varArgs && i == types.length - 1 ? text.substring(0, text.length() - 2) + "..." : text);
        }
        return line.toString();
    }

    static String name(Type type) {
        return type.getTypeName();
    }

    static void print(String kind, String... parts) {
        System.out.println(kind + "\t" + String.join("\t", parts));
    }
}
