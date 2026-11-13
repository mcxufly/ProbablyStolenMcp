using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Linq;

static class ItemTransformTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Denied(Action action)
    {
        try { action(); throw new Exception("Expected transform rejection"); }
        catch (ArgumentException) { checks++; }
        catch (InvalidOperationException) { checks++; }
    }
    static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Usage: <ManagedDir>");
        string managed = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var original = new GridShapeBuilder(3, 2).SetData("110100", 3).SetPosition(7, 11).SetOrientation(3).SetFlipped(true);
        // Bypass game constructors because only the existing shape property is used by the pure helper.
        var item = (GameItem)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        item.modifiedShape = original;
        var unchanged = ProbablyStolenMcp.ItemTransform.BuildShape(item);
        Check(!ReferenceEquals(unchanged, original), "shape is copied");
        Check(unchanged.orientation == 3 && unchanged.flipped && unchanged.minX == 7 && unchanged.minY == 11, "omitted values preserved");
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        foreach (bool flipped in new[] { false, true })
        {
            var copy = ProbablyStolenMcp.ItemTransform.BuildShape(item, rotation, flipped);
            Check(copy.orientation == rotation / 90 && copy.flipped == flipped, "absolute transform applied");
            Check(copy.width == 3 && copy.height == 2 && copy.GetLocal(0, 0) == 1 && copy.GetLocal(2, 0) == 0, "local cells preserved");
            Check(copy.globalWidth == (rotation % 180 == 0 ? 3 : 2) && copy.globalHeight == (rotation % 180 == 0 ? 2 : 3), "native occupied dimensions");
            var slot = new SlotMarker { itemGridShape = copy };
            ProbablyStolenMcp.ItemTransform.ValidatePlacementTransform(slot, rotation, flipped);
            checks++;
        }
        Check(original.orientation == 3 && original.flipped && original.minX == 7 && original.minY == 11, "source transform never mutated");
        var mirrorOnly = ProbablyStolenMcp.ItemTransform.BuildShape(item, flipped: false);
        Check(mirrorOnly.orientation == 3 && !mirrorOnly.flipped, "one omitted parameter preserves other");
        var rotationOnly = ProbablyStolenMcp.ItemTransform.BuildShape(item, rotation: 90);
        Check(rotationOnly.orientation == 1 && rotationOnly.flipped, "rotation-only preserves flip");
        foreach (int value in new[] { -90, 1, 45, 360, int.MaxValue }) Denied(() => ProbablyStolenMcp.ItemTransform.BuildShape(item, value));
        Denied(() => ProbablyStolenMcp.ItemTransform.ValidatePlacementTransform(new SlotMarker(), 0));
        Denied(() => ProbablyStolenMcp.ItemTransform.ValidatePlacementTransform(new SlotMarker { itemGridShape = unchanged }, 0));
        Denied(() => ProbablyStolenMcp.ItemTransform.ValidatePlacementTransform(new SlotMarker { itemGridShape = unchanged }, flipped: false));
        var description = ProbablyStolenMcp.ItemTransform.Describe(item);
        Check((int)description["rotation"] == 270 && (bool)description["flipped"] && (int)description["cell_x"] == 7 && (int)description["cell_y"] == 11, "readonly item placement description");
        Console.WriteLine($"PASS: {checks} native shape/copy/transform assertions; no Unity runtime exercised");
    }
}
