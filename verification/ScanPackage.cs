using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;

static class ScanPackage
{
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ScanPackage <ManagedDir> <mod-folder>"); return 2; }
        string managed = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        return Scan(managed, Path.GetFullPath(args[1]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Scan(string managed, string folder)
    {
        var root = XDocument.Load(Path.Combine(folder, "manifest.xml")).Root;
        var listed = root.Element("Assemblies").Elements("Assembly").Select(e => Path.Combine(folder, e.Value)).ToArray();
        var declared = root.Element("Capabilities").Elements("Capability").Select(e => e.Value).ToArray();
        // Outside Unity, ModLoader cannot discover Application.dataPath. Supply only
        // actual installed Managed assembly names, not arbitrary dependency exemptions.
        var gameAssemblies = System.IO.Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
        var result = ModScanner.Scan(listed, allowedReferences: gameAssemblies, folderPath: folder, localFolder: false);
        Console.WriteLine("Methods scanned: " + result.MethodsScanned);
        Console.WriteLine("Detected: " + string.Join(", ", result.Capabilities));
        Console.WriteLine("Declared: " + string.Join(", ", declared));
        Console.WriteLine("Refused: " + result.Refused);
        Console.WriteLine("Deprecated: " + result.UsesDeprecatedCapability);
        Console.WriteLine("Error: " + (result.Error ?? "none"));
        Console.WriteLine(result.FormatFindings(200));
        foreach (var finding in result.Pending) Console.WriteLine("Pending: " + finding);
        return result.Refused || result.UsesDeprecatedCapability || result.Capabilities.Except(declared).Any() ? 1 : 0;
    }
}
