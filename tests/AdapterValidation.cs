// Exercise the built adapter's pure argument helpers without touching Unity native calls.
// Reflection here is in an EXTERNAL verification tool, never shipped in the Mod DLL.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

static class AdapterValidation
{
    static Type adapter;
    static Type json;
    static int checks;
    static object Obj(string text) => json.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { text });
    static object Call(string name, params object[] args) => adapter.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static void Deny(string name, params object[] args)
    {
        try { Call(name, args); throw new Exception(name + " unexpectedly allowed input"); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { checks++; }
    }
    static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: <ManagedDir> <Mod DLL>");
        string managed = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        json = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Newtonsoft.Json.dll")).GetType("Newtonsoft.Json.Linq.JObject");
        adapter = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1])).GetType("ProbablyStolenMcp.GameAdapter");
        Check((int)Call("PageInt", Obj("{}"), "limit", 50, 1, 100) == 50, "default page limit");
        Check((int)Call("PageInt", Obj("{\"limit\":100}"), "limit", 50, 1, 100) == 100, "maximum page limit");
        foreach (string value in new[] { "true", "1.5", "101", "0", "2147483648", "\"1\"", "null" })
            Deny("PageInt", Obj("{\"limit\":" + value + "}"), "limit", 50, 1, 100);
        Check((int)Call("Option", "0", 2) == 0, "first option");
        foreach (string value in new[] { "-1", "2", "1.5", "true", "", "NaN" }) Deny("Option", value, 2);
        Check((float)Call("Number", Obj("{\"x\":1.5}"), "x") == 1.5f, "fractional coordinates");
        foreach (string value in new[] { "true", "\"5\"", "null", "1e100" }) Deny("Number", Obj("{\"x\":" + value + "}"), "x");
        Deny("RequiredString", Obj("{\"value\":1}"), "value", 0, 2048);
        Check((string)Call("RequiredString", Obj("{\"value\":\"\"}"), "value", 0, 2048) == "", "empty input allowed");
        string emoji = char.ConvertFromUtf32(0x1F600);
        string longValue = string.Concat(System.Linq.Enumerable.Repeat(emoji, 2048));
        string encoded = "{\"value\":\"" + longValue + "\"}";
        Check((string)Call("RequiredString", Obj(encoded), "value", 0, 2048) == longValue, "2048 Unicode codepoints allowed");
        Deny("RequiredString", Obj(encoded.Replace("\"}", "a\"}")), "value", 0, 2048);
        Deny("Fields", Obj("{\"unexpected\":1}"), new string[] { "limit" });
        Check(((string)Call("Clip", "a" + emoji + "z", 2)).Length != 2, "Clip does not leave an unpaired surrogate");
        Console.WriteLine($"PASS: {checks} built-adapter pure validation assertions (no Unity runtime exercised)");
    }
}
