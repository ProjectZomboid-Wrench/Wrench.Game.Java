// Regenerates Wrench.Game/Generated/ from the classes PZ exposes to Lua: dotnet run tools/GenerateLuaApi.cs -- <projectzomboid.jar>
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

var jar = Path.GetFullPath(args[0]);
var repo = Directory.GetCurrentDirectory();
var output = Path.Combine(repo, "Wrench.Game", "Generated");
var work = Path.Combine(Path.GetTempPath(), "wrench-luaapi-" + Environment.ProcessId);
Directory.CreateDirectory(work);

try
{
    var classList = Path.Combine(work, "exposed.txt");
    File.WriteAllLines(classList, ExposedClassNames(jar));
    Run("javac", ["--release", "22", "-cp", jar, "-d", work, Path.Combine(repo, "tools", "luaapi", "LuaApiDump.java")]);
    // PZ resolves some resources relative to its install folder, so run from there like the game does.
    var dump = Run("java", ["-Djava.awt.headless=true", "-cp", $"{work}{Path.PathSeparator}{jar}", "LuaApiDump", classList],
        Path.GetDirectoryName(jar));

    var model = Model.Parse(dump);
    if (Directory.Exists(output))
        Directory.Delete(output, recursive: true);
    Directory.CreateDirectory(output);
    var files = new Generator(model).Write(output);
    Console.WriteLine($"{model.Classes.Count} classes, {model.Globals.Count} globals -> {files} files in {output}");
}
finally
{
    Directory.Delete(work, recursive: true);
}

// Exposer.exposeAll calls setExposed(Class) once per class, plus static setExposed(Exposer) helpers on a few classes.
static List<string> ExposedClassNames(string jar)
{
    const string exposer = "zombie.Lua.LuaManager$Exposer";
    var helpers = Regex.Matches(Run("javap", ["-cp", jar, "-c", exposer]),
            @"Method (\S+)\.setExposed:\(Lzombie/Lua/LuaManager\$Exposer;\)V")
        .Select(m => m.Groups[1].Value.Replace('/', '.')).Distinct();
    var disassembly = Run("javap", ["-cp", jar, "-c", exposer, .. helpers]);

    var names = new SortedSet<string>(StringComparer.Ordinal);
    string? lastClass = null;
    foreach (var line in disassembly.Split('\n'))
    {
        var constant = Regex.Match(line, @"// class (\S+)$");
        if (constant.Success)
        {
            lastClass = constant.Groups[1].Value.Trim('"');
            continue;
        }
        if (line.Contains("setExposed:(Ljava/lang/Class;)V") && lastClass is not null && !lastClass.StartsWith('['))
            names.Add(lastClass.Replace('/', '.'));
        lastClass = null;
    }
    return [.. names];
}

static string Run(string file, string[] arguments, string? workingDirectory = null)
{
    var start = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
    if (workingDirectory is not null)
        start.WorkingDirectory = workingDirectory;
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (stderr.Result.Length > 0)
        Console.Error.Write(stderr.Result);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{file} exited with {process.ExitCode}");
    return stdout;
}

sealed record JType(string Name, JType[] Args, int Rank)
{
    private static readonly HashSet<string> Primitives = ["boolean", "byte", "short", "char", "int", "long", "float", "double", "void"];

    public static readonly JType Object = new("java.lang.Object", [], 0);

    public bool IsVoid => Name == "void" && Rank == 0;

    public bool IsTypeVariable => !Name.Contains('.') && !Primitives.Contains(Name);

    // Type.getTypeName() text: "java.util.List<a.B$C>", "int[]", "? extends a.B", "T".
    public static JType Parse(string text)
    {
        text = text.Trim();
        var rank = 0;
        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            rank++;
            text = text[..^2];
        }
        if (text.StartsWith("? extends ", StringComparison.Ordinal))
            return Parse(text["? extends ".Length..]) with { Rank = rank };
        if (text.StartsWith('?'))
            return Object with { Rank = rank };
        if (!text.EndsWith('>'))
            return new JType(StripGenerics(text), [], rank);

        var depth = 0;
        var open = text.Length - 1;
        for (; open >= 0; open--)
        {
            depth += text[open] switch { '>' => 1, '<' => -1, _ => 0 };
            if (depth == 0)
                break;
        }
        var arguments = SplitTopLevel(text[(open + 1)..^1]).Select(Parse).ToArray();
        return new JType(StripGenerics(text[..open]), arguments, rank);
    }

    private static string StripGenerics(string text)
    {
        while (text.Contains('<'))
            text = Regex.Replace(text, "<[^<>]*>", "");
        return text;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '<' => 1, '>' => -1, _ => 0 };
            if (text[i] == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }
}

sealed record JMethod(bool IsStatic, string Name, string DeclaringClass, JType Return, JType[] Parameters);

sealed class JClass(string name, string? superclass, string flags, string simpleName)
{
    public string Name { get; } = name;
    public string? Superclass { get; } = superclass;
    public bool IsInterface { get; } = flags.Contains("interface");
    public bool IsAbstract { get; } = flags.Contains("abstract");
    public string SimpleName { get; } = simpleName;
    public List<JMethod> Methods { get; } = [];
    public List<JType[]> Constructors { get; } = [];
    public List<(string Name, JType Type)> Fields { get; } = [];
}

sealed record JGlobal(string LuaName, JType Return, JType[] Parameters);

sealed class Model
{
    public List<JClass> Classes { get; } = [];
    public List<JGlobal> Globals { get; } = [];

    public static Model Parse(string dump)
    {
        var model = new Model();
        JClass? current = null;
        foreach (var line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "C":
                    current = new JClass(f[1], f[2] == "-" ? null : f[2], f[3], f[4]);
                    model.Classes.Add(current);
                    break;
                case "M":
                    current!.Methods.Add(new JMethod(f[1] == "1", f[2], f[3], JType.Parse(f[4]), [.. f[5..].Select(JType.Parse)]));
                    break;
                case "K":
                    current!.Constructors.Add([.. f[1..].Where(p => p.Length > 0).Select(JType.Parse)]);
                    break;
                case "F":
                    current!.Fields.Add((f[1], JType.Parse(f[2])));
                    break;
                case "G":
                    model.Globals.Add(new JGlobal(f[1], JType.Parse(f[2]), [.. f[3..].Select(JType.Parse)]));
                    break;
            }
        }
        return model;
    }
}

sealed class Generator
{
    // Methods every Java object has; JavaObject covers equality and ToString.
    private static readonly HashSet<string> SkippedMethods =
    [
        "toString", "equals", "hashCode", "getClass", "wait", "notify", "notifyAll", "clone", "finalize",
        "describeConstable", "getDeclaringClass", "compareTo",
    ];

    private static readonly HashSet<string> ListTypes =
        ["java.util.List", "java.util.ArrayList", "java.util.LinkedList", "java.util.Stack", "java.util.Vector"];

    private static readonly Dictionary<string, string> Builtins = new()
    {
        ["boolean"] = "bool", ["byte"] = "sbyte", ["short"] = "short", ["char"] = "char", ["int"] = "int",
        ["long"] = "long", ["float"] = "float", ["double"] = "double", ["void"] = "void",
        ["java.lang.String"] = "string", ["java.lang.CharSequence"] = "string", ["java.lang.Object"] = "object",
        ["java.lang.Boolean"] = "bool?", ["java.lang.Byte"] = "sbyte?", ["java.lang.Short"] = "short?",
        ["java.lang.Character"] = "char?", ["java.lang.Integer"] = "int?", ["java.lang.Long"] = "long?",
        ["java.lang.Float"] = "float?", ["java.lang.Double"] = "double?", ["java.lang.Number"] = "double?",
    };

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
        "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    // Members of JavaObject (and object) a generated member must not replace.
    private static readonly HashSet<string> ReservedMembers =
    [
        "AsGameObject", "As", "Equals", "GetHashCode", "ToString", "GetType", "JavaCall", "JavaCallVoid",
        "JavaStatic", "JavaStaticVoid", "JavaField", "MemberwiseClone", "Finalize", "ReferenceEquals",
    ];

    private readonly Dictionary<string, JClass> _classes;
    private readonly Dictionary<string, string> _csNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uniqueSimpleNames;

    public Generator(Model model)
    {
        _classes = model.Classes.Where(c => !c.Name.StartsWith("java.", StringComparison.Ordinal))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);
        Globals = model.Globals;
        // Kahlua binds each exposed class to a global named after its simple name; duplicates are ambiguous.
        _uniqueSimpleNames = model.Classes.GroupBy(c => c.SimpleName).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        foreach (var name in _classes.Keys)
            _csNames[name] = CsFullName(name);
    }

    private List<JGlobal> Globals { get; }

    public int Write(string directory)
    {
        var files = 0;
        foreach (var package in _classes.Values.Where(c => Outer(c.Name) is not { } outer || !_classes.ContainsKey(outer)).GroupBy(c => Package(c.Name)))
        {
            var code = new StringBuilder();
            Header(code);
            code.AppendLine($"namespace {Namespace(package.Key)};").AppendLine();
            foreach (var type in package.OrderBy(c => c.Name, StringComparer.Ordinal))
                WriteClass(code, type, "");
            File.WriteAllText(Path.Combine(directory, package.Key + ".cs"), code.ToString());
            files++;
        }

        var registry = new StringBuilder();
        Header(registry);
        registry.AppendLine("namespace Zomboid;").AppendLine()
            .AppendLine("internal static partial class JavaTypes")
            .AppendLine("{")
            .AppendLine("    static partial void Register(Dictionary<string, Func<global::Wrench.GameObject, JavaObject>> byJavaName,")
            .AppendLine("                                 Dictionary<Type, Func<global::Wrench.GameObject, JavaObject>> byType)")
            .AppendLine("    {");
        foreach (var (java, cs) in _csNames.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            registry.AppendLine($"        byJavaName[\"{java}\"] = h => new global::{cs}(h);");
            registry.AppendLine($"        byType[typeof(global::{cs})] = h => new global::{cs}(h);");
        }
        registry.AppendLine("    }").AppendLine("}");
        File.WriteAllText(Path.Combine(directory, "JavaTypes.Registry.cs"), registry.ToString());

        File.WriteAllText(Path.Combine(directory, "Globals.cs"), WriteGlobals());
        return files + 2;
    }

    private static void Header(StringBuilder code) =>
        code.AppendLine("// <auto-generated>tools/GenerateLuaApi.cs from the classes PZ exposes to Lua. Don't edit.</auto-generated>")
            .AppendLine("#nullable disable")
            .AppendLine();

    private void WriteClass(StringBuilder code, JClass type, string indent)
    {
        var name = CsSimpleName(type.Name);
        var baseType = BaseClass(type) is { } b ? "global::" + _csNames[b.Name] : "global::Zomboid.JavaObject";
        var nested = _classes.Values.Where(c => Outer(c.Name) == type.Name).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        var reserved = new HashSet<string>(ReservedMembers) { name };
        foreach (var inner in nested)
            reserved.Add(CsSimpleName(inner.Name));
        var members = new Members(reserved);

        code.AppendLine($"{indent}/// <summary>Java <c>{type.Name.Replace('$', '.')}</c>.</summary>")
            .AppendLine($"{indent}public partial class {name} : {baseType}")
            .AppendLine($"{indent}{{")
            .AppendLine($"{indent}    protected internal {name}(global::Wrench.GameObject handle) : base(handle)")
            .AppendLine($"{indent}    {{")
            .AppendLine($"{indent}    }}");

        var inherited = BaseClass(type) is { } baseClass ? InstanceKeys(baseClass) : [];
        var instance = type.Methods
            .Where(m => !m.IsStatic && !SkippedMethods.Contains(m.Name) && IsJavaIdentifier(m.Name))
            .GroupBy(Key).Select(g => g.First())
            .Where(m => !inherited.Contains(Key(m)))
            .ToList();

        var body = new StringBuilder();
        var asProperties = PairProperties(instance, members);
        foreach (var (propertyName, getter, setter) in asProperties)
        {
            var csType = Map(getter.Return);
            body.AppendLine($"{indent}    public {csType} {propertyName}")
                .AppendLine($"{indent}    {{")
                .AppendLine($"{indent}        get => JavaCall<{csType}>(this, \"{getter.Name}\");")
                .AppendLine($"{indent}        set => JavaCallVoid(this, \"{setter.Name}\", value);")
                .AppendLine($"{indent}    }}");
        }
        var paired = asProperties.SelectMany(p => new[] { p.Getter, p.Setter }).ToHashSet();
        foreach (var method in instance.Where(m => !paired.Contains(m)))
        {
            var parameters = Parameters(method.Parameters);
            var csName = members.Method(Pascal(method.Name), parameters.Types);
            var returnType = Map(method.Return);
            var call = method.Return.IsVoid
                ? $"JavaCallVoid(this, \"{method.Name}\"{parameters.Arguments})"
                : $"JavaCall<{returnType}>(this, \"{method.Name}\"{parameters.Arguments})";
            body.AppendLine($"{indent}    public {returnType} {csName}({parameters.Declaration}) => {call};");
        }

        // Statics live in the class's Lua global, which is only unambiguous if no other exposed class shares the name.
        if (_uniqueSimpleNames.Contains(type.SimpleName))
        {
            var table = type.SimpleName;
            if (!type.IsAbstract && !type.IsInterface)
            {
                foreach (var constructor in type.Constructors)
                {
                    var parameters = Parameters(constructor);
                    var csName = members.Method("New", parameters.Types);
                    body.AppendLine($"{indent}    public static {name} {csName}({parameters.Declaration}) => " +
                                    $"JavaStatic<{name}>(\"{table}\", \"new\"{parameters.Arguments});");
                }
            }
            foreach (var method in type.Methods.Where(m => m.IsStatic && m.DeclaringClass == type.Name && IsJavaIdentifier(m.Name))
                         .GroupBy(Key).Select(g => g.First()))
            {
                var parameters = Parameters(method.Parameters);
                var csName = members.Method(Pascal(method.Name), parameters.Types);
                var returnType = Map(method.Return);
                var call = method.Return.IsVoid
                    ? $"JavaStaticVoid(\"{table}\", \"{method.Name}\"{parameters.Arguments})"
                    : $"JavaStatic<{returnType}>(\"{table}\", \"{method.Name}\"{parameters.Arguments})";
                body.AppendLine($"{indent}    public static {returnType} {csName}({parameters.Declaration}) => {call};");
            }
            foreach (var (fieldName, fieldType) in type.Fields.Where(f => IsJavaIdentifier(f.Name)))
            {
                var csName = members.Property(fieldName);
                var csType = Map(fieldType);
                body.AppendLine($"{indent}    public static {csType} {csName} => JavaField<{csType}>(\"{table}\", \"{fieldName}\");");
            }
        }

        code.Append(body);
        foreach (var inner in nested)
        {
            code.AppendLine();
            WriteClass(code, inner, indent + "    ");
        }
        code.AppendLine($"{indent}}}").AppendLine();
    }

    private List<(string Name, JMethod Getter, JMethod Setter)> PairProperties(List<JMethod> methods, Members members)
    {
        var result = new List<(string, JMethod, JMethod)>();
        var csMethodNames = methods.Select(m => Pascal(m.Name)).ToHashSet();
        foreach (var getter in methods.Where(m => m.Parameters.Length == 0 && !m.Return.IsVoid))
        {
            var match = Regex.Match(getter.Name, "^(get|is)([A-Z].*)$");
            if (!match.Success)
                continue;
            var suffix = match.Groups[2].Value;
            var setter = methods.FirstOrDefault(m => m.Name == "set" + suffix && m.Parameters.Length == 1
                                                     && m.Return.IsVoid && Map(m.Parameters[0]) == Map(getter.Return));
            if (setter is null)
                continue;
            var propertyName = match.Groups[1].Value == "is" ? "Is" + suffix : suffix;
            if (csMethodNames.Contains(propertyName) || !members.TryClaimProperty(propertyName))
                continue;
            result.Add((propertyName, getter, setter));
        }
        return result;
    }

    private HashSet<string> InstanceKeys(JClass type) =>
        type.Methods.Where(m => !m.IsStatic).Select(Key).ToHashSet();

    private string Key(JMethod method) => method.Name + "(" + string.Join(",", method.Parameters.Select(Map)) + ")";

    private (string Declaration, string Arguments, string Types) Parameters(JType[] types)
    {
        var used = new HashSet<string>();
        var declarations = new List<string>();
        var names = new List<string>();
        foreach (var type in types)
        {
            var csType = Map(type);
            var baseName = ParameterName(type, csType);
            var parameterName = baseName;
            for (var i = 2; !used.Add(parameterName); i++)
                parameterName = baseName + i;
            var escaped = Keywords.Contains(parameterName) ? "@" + parameterName : parameterName;
            declarations.Add($"{csType} {escaped}");
            names.Add(escaped);
        }
        return (string.Join(", ", declarations), string.Concat(names.Select(n => ", " + n)), string.Join(",", types.Select(Map)));
    }

    private string ParameterName(JType type, string csType)
    {
        if (type.Rank > 0)
            return "array";
        if (csType.StartsWith("global::Zomboid.JavaList", StringComparison.Ordinal))
            return "list";
        if (_classes.ContainsKey(type.Name))
            return Camel(CsSimpleName(type.Name));
        return csType switch
        {
            "string" => "text",
            "bool" or "bool?" => "flag",
            "global::Wrench.GameObject" => "obj",
            _ => "value",
        };
    }

    private string Map(JType type)
    {
        if (type.Rank > 0)
            return "global::Wrench.GameObject";
        if (type.IsTypeVariable)
            return "object";
        if (Builtins.TryGetValue(type.Name, out var builtin))
            return builtin;
        if (ListTypes.Contains(type.Name))
        {
            var element = type.Args.Length == 1 ? Map(type.Args[0]) : "object";
            return $"global::Zomboid.JavaList<{(element == "void" ? "object" : element)}>";
        }
        return _csNames.TryGetValue(type.Name, out var cs) ? "global::" + cs : "global::Wrench.GameObject";
    }

    // Only exposed superclasses are in the dump; methods inherited from others are listed on the class itself.
    private JClass? BaseClass(JClass type) =>
        !type.IsInterface && type.Superclass is { } name && _classes.TryGetValue(name, out var found) ? found : null;

    private string WriteGlobals()
    {
        var code = new StringBuilder();
        Header(code);
        code.AppendLine("namespace Zomboid;").AppendLine()
            .AppendLine("/// <summary>PZ's global Lua functions (getPlayer, getText, ...), typed. <c>using static Zomboid.Globals;</c> reads like Lua.</summary>")
            .AppendLine("public static class Globals")
            .AppendLine("{");
        var members = new Members(["Globals"]);
        foreach (var global in Globals.Where(g => IsJavaIdentifier(g.LuaName)).OrderBy(g => g.LuaName, StringComparer.Ordinal))
        {
            var parameters = Parameters(global.Parameters);
            var csName = members.Method(Pascal(global.LuaName), parameters.Types);
            var returnType = Map(global.Return);
            var arguments = $"[{parameters.Arguments.TrimStart(',', ' ')}]";
            var call = global.Return.IsVoid
                ? $"JavaObject.CallGlobalVoid(\"{global.LuaName}\", {arguments})"
                : $"JavaObject.CallGlobal<{returnType}>(\"{global.LuaName}\", {arguments})";
            code.AppendLine($"    public static {returnType} {csName}({parameters.Declaration}) => {call};");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private string CsFullName(string javaName)
    {
        var outer = Outer(javaName);
        if (outer is not null && _classes.ContainsKey(outer))
            return CsFullName(outer) + "." + CsSimpleName(javaName);
        return Namespace(Package(javaName)) + "." + CsSimpleName(javaName);
    }

    // Nested classes become nested types when their outer class is exposed, otherwise Outer_Inner.
    private string CsSimpleName(string javaName)
    {
        var simple = javaName[(javaName.LastIndexOf('.') + 1)..];
        var outer = Outer(javaName);
        return outer is not null && _classes.ContainsKey(outer) ? simple[(simple.LastIndexOf('$') + 1)..] : simple.Replace('$', '_');
    }

    private static string? Outer(string javaName)
    {
        var dollar = javaName.LastIndexOf('$');
        return dollar < 0 ? null : javaName[..dollar];
    }

    private static string Package(string javaName) => javaName[..javaName.LastIndexOf('.')];

    private static string Namespace(string package)
    {
        var segments = package.Split('.').Select(Pascal).ToList();
        if (segments[0] == "Zombie")
            segments[0] = "Zomboid";
        else
            segments.Insert(0, "Zomboid");
        return string.Join(".", segments);
    }

    private static bool IsJavaIdentifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // Hands out member names unique within one class: C# can't overload on return type, mix static and instance
    // members with one signature, or name a member like its class or a nested type.
    private sealed class Members(HashSet<string> reserved)
    {
        private readonly HashSet<string> _properties = [];
        private readonly HashSet<string> _methods = [];
        private readonly HashSet<string> _signatures = [];

        public string Method(string name, string parameterTypes)
        {
            var baseName = reserved.Contains(name) || _properties.Contains(name) ? name + "_" : name;
            var candidate = baseName;
            for (var i = 2; !_signatures.Add(candidate + "(" + parameterTypes + ")"); i++)
                candidate = baseName + "_" + i;
            _methods.Add(candidate);
            return candidate;
        }

        public bool TryClaimProperty(string name)
        {
            if (reserved.Contains(name) || _properties.Contains(name) || _methods.Contains(name))
                return false;
            _properties.Add(name);
            return true;
        }

        public string Property(string name)
        {
            var candidate = name;
            while (!TryClaimProperty(candidate))
                candidate += "_";
            return candidate;
        }
    }
}
