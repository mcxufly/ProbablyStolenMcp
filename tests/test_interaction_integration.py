"""Source-level integration guards for 0.3.0; do not claim Unity runtime coverage."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
GAME = (ROOT / 'src/GameAdapter.cs').read_text(encoding='utf-8')
DROP = (ROOT / 'src/ItemDropAdapter.cs').read_text(encoding='utf-8')
EQUIP = (ROOT / 'src/EquipmentAdapter.cs').read_text(encoding='utf-8')


class InteractionIntegrationTests(unittest.TestCase):
    def test_drop_resolves_both_visible_handles_and_consumes_snapshot(self):
        method = GAME.split('case "drop_item":', 1)[1].split('case "equipment_action":', 1)[0]
        self.assertIn('GetEntry(arguments)', method)
        self.assertIn('Mutate(dropSource', method)
        self.assertIn('IsLive(dropTarget)', method)
        self.assertIn('ItemDropAdapter.Drop(sourceItem, targetItem, dropAmount)', method)
        self.assertNotIn('Number(arguments', method)

    def test_equipment_state_is_bound_to_identity_count_and_parent(self):
        method = GAME.split('private JObject EquipmentAction(', 1)[1].split('private JObject Mutate(', 1)[0]
        for guard in ('CheckSnapshot(args)', 'SensitiveScreen()', 'ReferenceEquals(current, observedEquipment)',
                      'observedEquipmentCount', 'observedEquipmentParent', 'IsLive(source)', 'IsLive(target)',
                      'finally { Reset(); }'):
            self.assertIn(guard, method)
        self.assertIn('selected item is obscured', method)
        self.assertIn('IsLive(selectedEntry)', method)
        self.assertIn('equipment["source_visible"]', GAME)
        self.assertIn('equipment["source_handle"]', GAME)
        self.assertIn('observedEquipment == null ? null : entries.FirstOrDefault', GAME)
        self.assertIn('ReferenceEquals(ItemSelectHandler.current, observedSelectionHandler)', method)

    def test_equipment_uses_native_transitions_and_failure_cleanup(self):
        for native in ('handler.TryEquipItem(item)', 'source.MayTarget(target)', 'source.CanTarget(target)',
                       'ItemBehaviourManager.current.Target(source, target)', 'source.Target(target)',
                       'source.MayActivateSlotItem()', 'source.CanActivateSlotItem()',
                       'ModReflect.Call(handler, "ContinueEquipAfterUse")', 'handler.EndSelect()', 'handler.Cancel()'):
            self.assertIn(native, EQUIP)
        for forbidden in ('onUsed', 'unitCount--', '.ModifyTag(', 'Input.mousePosition'):
            # onUsed may be mentioned in comments; direct invocation must never be present.
            if forbidden == 'onUsed':
                self.assertNotIn('.onUsed(', EQUIP)
            else:
                self.assertNotIn(forbidden, EQUIP)

    def test_drop_is_movement_only_not_target_use(self):
        for guard in ('source.MayRemove()', 'source.MaxNumRemove()', 'IsItemEffectivelyOwned(target)',
                      'MayPlayerInsertInto(target)', 'target.contentWindow.isLocked', 'ValidateContainerSlot(',
                      'target.GetStackSlot(source)', 'slot.TryAcceptOnce(', 'MaxContainerPasses = 40'):
            self.assertIn(guard, DROP)
        for forbidden in ('.Target(', '.MayTarget(', '.CanTarget(', 'UncheckedAccept(', 'unitCount -='):
            self.assertNotIn(forbidden, DROP)

    def test_parent_cycle_and_slot_ownership_checks_are_present(self):
        for guard in ('ReferenceEquals(source, target)', 'IsAncestor(target, source)',
                      'IsAncestor(slot.inventory, source)', 'IsAncestor(slot.inventory, target)',
                      'slot.inventory.IsInsertLocked()', 'MaxAncestorNodes = 4096'):
            self.assertIn(guard, DROP)


if __name__ == '__main__':
    unittest.main()
