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
 * Tab-separated lines: C class super flags simpleName | M static name declaringClass return params... | K params... |
 * F name type | G luaName return params...
 */
public final class LuaApiDump {
    public static void main(String[] args) throws Exception {
        var sorted = new TreeMap<String, Class<?>>();
        var loader = LuaApiDump.class.getClassLoader();
        for (var name : Files.readAllLines(Path.of(args[0]))) {
            if (name.isBlank())
                continue;
            try {
                sorted.put(name, Class.forName(name.trim(), false, loader));
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
        var superclass = type.getSuperclass();
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
            for (var parameter : method.getGenericParameterTypes())
                line.append('\t').append(name(parameter));
            print("M", line.toString());
        }
        if (!Modifier.isAbstract(type.getModifiers()) && !type.isInterface()) {
            for (Constructor<?> constructor : type.getConstructors())
                print("K", String.join("\t", Arrays.stream(constructor.getGenericParameterTypes()).map(LuaApiDump::name).toList()));
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
            for (var parameter : method.getGenericParameterTypes())
                line.append('\t').append(name(parameter));
            print("G", line.toString());
        }
    }

    static String name(Type type) {
        return type.getTypeName();
    }

    static void print(String kind, String... parts) {
        System.out.println(kind + "\t" + String.join("\t", parts));
    }
}
