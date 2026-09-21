using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;

// Dumps the full type/member surface of one or more managed assemblies to text,
// for offline recon of Broken Arrow's IL2CPP interop assemblies.
//
// Usage: DumpTypes <outDir> <assembly1.dll> [assembly2.dll ...]

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DumpTypes <outDir> <asm1> [asm2 ...]");
            return 2;
        }

        string outDir = args[0];
        Directory.CreateDirectory(outDir);

        foreach (string asmPath in args.Skip(1))
        {
            try
            {
                DumpOne(asmPath, outDir);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"FAILED {asmPath}: {e.Message}");
            }
        }
        return 0;
    }

    private static void DumpOne(string asmPath, string outDir)
    {
        string stem = Path.GetFileNameWithoutExtension(asmPath);
        Console.WriteLine($"Loading {asmPath} ...");
        var asm = AssemblyDefinition.FromFile(asmPath);
        var module = asm.ManifestModule;

        var types = module.GetAllTypes().ToList();
        Console.WriteLine($"  {types.Count} types");

        // 1) Full member dump (types, base, fields, methods).
        string fullPath = Path.Combine(outDir, stem + ".full.txt");
        // 2) Compact index: one line per type "namespace.Type : Base".
        string idxPath = Path.Combine(outDir, stem + ".types.txt");

        using var full = new StreamWriter(fullPath, false, new UTF8Encoding(false));
        using var idx = new StreamWriter(idxPath, false, new UTF8Encoding(false));

        foreach (var t in types.OrderBy(t => Safe(() => t.Namespace?.ToString() ?? ""))
                                .ThenBy(t => Safe(() => t.Name?.ToString() ?? "")))
        {
            string ns = Safe(() => t.Namespace?.ToString() ?? "");
            string name = Safe(() => t.Name?.ToString() ?? "<?>");
            string full1 = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            string baseName = Safe(() => t.BaseType?.FullName ?? "");
            string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" :
                          t.IsValueType ? "struct" : "class";

            idx.WriteLine($"{full1} : {baseName}  [{kind}]");

            full.WriteLine($"==== {kind} {full1}" + (string.IsNullOrEmpty(baseName) ? "" : $" : {baseName}") + " ====");

            // interfaces
            foreach (var i in Safe(() => (IEnumerable<InterfaceImplementation>)t.Interfaces) ?? Enumerable.Empty<InterfaceImplementation>())
            {
                string iname = Safe(() => i.Interface?.FullName ?? "");
                if (!string.IsNullOrEmpty(iname)) full.WriteLine($"    :impl {iname}");
            }

            // fields
            foreach (var f in t.Fields)
            {
                string ft = Safe(() => f.Signature?.FieldType?.ToString() ?? "?");
                string mods = (f.IsStatic ? "static " : "") + (f.IsPublic ? "public " : f.IsPrivate ? "private " : "");
                full.WriteLine($"    field {mods}{ft} {Safe(() => f.Name?.ToString() ?? "?")}");
            }

            // methods
            foreach (var m in t.Methods)
            {
                string ret = Safe(() => m.Signature?.ReturnType?.ToString() ?? "?");
                string ps = Safe(() => string.Join(", ", m.Parameters.Select(p => (p.ParameterType?.ToString() ?? "?") + " " + (p.Name ?? "")))) ?? "";
                string mods = (m.IsStatic ? "static " : "") + (m.IsPublic ? "public " : m.IsPrivate ? "private " : "");
                full.WriteLine($"    method {mods}{ret} {Safe(() => m.Name?.ToString() ?? "?")}({ps})");
            }

            full.WriteLine();
        }

        Console.WriteLine($"  -> {idxPath}");
        Console.WriteLine($"  -> {fullPath}");
    }

    private static T Safe<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }
}
