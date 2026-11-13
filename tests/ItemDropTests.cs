using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ProbablyStolenMcp;

static class ItemDropTests
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Denied(Action action)
    {
        try { action(); throw new Exception("Expected drop rejection"); }
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
    static GameItem Item() => (GameItem)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
    static GameGridInventory Inventory() => (GameGridInventory)RuntimeHelpers.GetUninitializedObject(typeof(GameGridInventory));
    static void Parent(GameInventory inventory, GraphNodeStorage parent) =>
        typeof(GameInventory).GetProperty("parent").SetValue(inventory, parent);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var source = Item(); var target = Item();
        ItemDropAdapter.ValidatePair(source, target, 1); checks++;
        ItemDropAdapter.ValidatePair(source, target, int.MaxValue); checks++;
        Denied(() => ItemDropAdapter.ValidatePair(null, target, 1));
        Denied(() => ItemDropAdapter.ValidatePair(source, null, 1));
        Denied(() => ItemDropAdapter.ValidatePair(source, target, 0));
        Denied(() => ItemDropAdapter.ValidatePair(source, target, -1));
        Denied(() => ItemDropAdapter.ValidatePair(source, source, 1));
        var insideSource = Inventory(); Parent(insideSource, source); target.parentInventory = insideSource;
        Denied(() => ItemDropAdapter.ValidatePair(source, target, 1));
        Check(target.parentInventory == insideSource && source.parentInventory == null, "rejection does not detach or mutate source/target");
        target.parentInventory = null;
        var insideTarget = Inventory(); Parent(insideTarget, target); source.parentInventory = insideTarget;
        ItemDropAdapter.ValidatePair(source, target, 1); checks++; // Moving a child back into its container is not a cycle.
        Denied(() => ItemDropAdapter.ValidateContainerSlot(null, source, target));
        Denied(() => ItemDropAdapter.ValidateContainerSlot(new SlotMarker { item = target }, source, target));
        var unrelated = Inventory();
        Denied(() => ItemDropAdapter.ValidateContainerSlot(new SlotMarker { item = source, inventory = unrelated, itemGridShape = new GridShapeBuilder(1, 1) }, source, target));
        Denied(() => ItemDropAdapter.ValidateContainerSlot(new SlotMarker { item = source, targetItem = source, inventory = insideTarget, itemGridShape = new GridShapeBuilder(1, 1) }, source, target));
        insideTarget.overrideLockInsert = true;
        Denied(() => ItemDropAdapter.ValidateContainerSlot(new SlotMarker { item = source, inventory = insideTarget, itemGridShape = new GridShapeBuilder(1, 1) }, source, target));
        Check(insideTarget.overrideLockInsert && source.parentInventory == insideTarget, "locked slot validation leaves graph untouched");
        Console.WriteLine($"PASS: {checks} native drop boundary/graph assertions; no Unity gameplay calls exercised");
    }
}
