using System.Reflection;
using System.Runtime.Loader;
using System.Linq;
using System;
using System.Collections.Generic;

var asmDir = System.IO.Path.GetDirectoryName(args[0]);
var loadContext = new AssemblyLoadContext("inspect", isCollectible: true);
loadContext.Resolving += (ctx, name) =>
{
    try
    {
        var path = System.IO.Path.Combine(asmDir, name.Name + ".dll");
        if (System.IO.File.Exists(path))
            return ctx.LoadFromAssemblyPath(path);
    }
    catch { }
    return null;
};

var asm = loadContext.LoadFromAssemblyPath(args[0]);

Console.WriteLine("=== ALL PUBLIC TYPES IN RELOAD ===");
foreach (var t in asm.GetTypes().OrderBy(x => x.FullName))
{
    string fn = t.FullName ?? "";
    if (fn.Contains("Private") || fn.Contains("<>")) continue;

    Console.WriteLine($"\n=== {fn} ===");
    Console.WriteLine($"  IsInterface={t.IsInterface} IsClass={t.IsClass} IsEnum={t.IsEnum} BaseType={t.BaseType?.FullName}");

    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  M: {m.ReturnType.Name} {m.Name}({parms})");
    }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  P: {p.PropertyType.Name} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
    {
        if (f.IsLiteral) Console.WriteLine($"  F: {f.FieldType.Name} {f.Name} = {f.GetRawConstantValue()}");
        else Console.WriteLine($"  F: {f.FieldType.Name} {f.Name}");
    }
}

Console.WriteLine("\n\n=== METHODS WITH TOGGLE/STANCE/ACTIVE/ATTACK/SLOT/BAR IN NAME ===");
foreach (var t in asm.GetTypes().OrderBy(x => x.FullName))
{
    string fn = t.FullName ?? "";
    if (fn.Contains("Private") || fn.Contains("<>")) continue;
    try
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string mn = m.Name;
            if (mn.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("stance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("buff", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("hotbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("slot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mn.IndexOf("shortcut", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"  {fn}.{m.ReturnType.Name} {m.Name}({parms})");
            }
        }
    }
    catch { Console.WriteLine($"  (failed to load {fn})"); }
}

loadContext.Unload();
