using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ProbablyStolenMcp;

static class EquipmentTests
{
    static int assertions;
    static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    static void Main()
    {
        var idle = EquipmentAdapter.DescribeState(true, true, false, false, false);
        Check((string)idle["mode"] == "idle", "idle state");
        Check(!(bool)idle["selected"] && !(bool)idle["equipped"], "idle has no selected/equipped state");
        var selecting = EquipmentAdapter.DescribeState(true, true, true, false, false);
        Check((string)selecting["mode"] == "selected_waiting_for_target", "selection isn't consumption");
        var equipped = EquipmentAdapter.DescribeState(true, true, true, true, false);
        Check((string)equipped["mode"] == "equipped_waiting_for_target", "persistent equip mode");
        Check((bool)equipped["equip_is_not_consumption"], "equip never claims consumption");
        Check((string)equipped["supported_target_kind"] == "visible_item_only", "no arbitrary world/inventory target");
        var disabled = EquipmentAdapter.DescribeState(true, false, true, true, false);
        Check(!(bool)disabled["handler_ready"], "disabled handler unavailable");
        var destroyed = EquipmentAdapter.DescribeState(true, true, true, true, true);
        Check((bool)destroyed["selected_item_destroyed"], "destroyed state is explicit");
        var missing = EquipmentAdapter.DescribeState(false, false, false, false, false);
        Check(!(bool)missing["handler_present"], "missing handler explicit");
        Check(!equipped.ContainsKey("identifier") && !equipped.ContainsKey("name") && !equipped.ContainsKey("unique_id"), "no hidden item identity in description");

        // Source guards verify the selected implementation path, not runtime Unity behaviour.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        string source = File.ReadAllText(Path.Combine(root, "src/EquipmentAdapter.cs"));
        Check(source.Contains("handler.TryEquipItem(item)"), "equip uses native state machine");
        Check(source.Contains("handler.Cancel()") && source.Contains("handler.EndSelect()"), "equipped and transient cancel both supported");
        Check(source.Contains("ReferenceEquals(CurrentItem(), source)"), "source must equal native equipped reference");
        Check(source.IndexOf("ItemBehaviourManager.current.Target(source, target)", StringComparison.Ordinal) < source.IndexOf("source.Target(target)", StringComparison.Ordinal), "native behaviour precedes item target");
        Check(source.Contains("source.MayTarget(target) && source.CanTarget(target)"), "recheck native target permission after behaviours");
        Check(source.Contains("source.MayActivateSlotItem() || !source.CanActivateSlotItem()"), "self activation has native permission gates");
        Check(source.Contains("ModReflect.Call(handler, \"ContinueEquipAfterUse\")"), "native retain/exit cleanup preserved");
        Check(!source.Contains("Input.mousePosition") && !source.Contains("Raycast("), "no mouse-position target substitution");
        Check(!source.Contains("onUsed") || source.Contains("never invoke onUsed"), "does not manually invoke consumption hook");
        Check(!source.Contains("unitCount--") && !source.Contains("GetTagReadonly(") && !source.Contains("GetUniqueID("), "no fake consumption or observer tag/id side effect");
        Console.WriteLine($"PASS: {assertions} equipment state/source assertions; no Unity runtime exercised");
    }
}
