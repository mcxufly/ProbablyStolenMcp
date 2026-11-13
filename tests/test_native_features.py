"""Source guards for 0.2.0 adapter integration; not Unity gameplay tests."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
GAME = (ROOT / 'src/GameAdapter.cs').read_text()
DIALOGUE = (ROOT / 'src/DialogueAdapter.cs').read_text()


class NativeFeatureIntegrationTests(unittest.TestCase):
    def test_dialogue_tools_are_routed_and_scene_gated(self):
        for operation in ('dialogue_history', 'dialogue_status', 'dialogue_action'):
            self.assertIn('case "' + operation + '"', GAME)
        self.assertIn('new DialogueAdapter(() => InRun() && !SensitiveScreen(), Allowed)', GAME)
        self.assertIn('finally { Reset(); }', GAME)
        self.assertIn('dialogue.InvalidateActions()', GAME)
        self.assertIn('dialogueScene != SceneManager.GetActiveScene().handle', GAME)

    def test_recorder_copies_native_queue_without_own_logging_hooks(self):
        self.assertIn('PlayerStore.Instance.dialogHistory', DIALOGUE)
        self.assertIn('queue.ToArray()', DIALOGUE)
        self.assertIn('historyOwner != PlayerStore.Instance', DIALOGUE)
        self.assertIn('["source"] = "native_recorder"', DIALOGUE)
        self.assertNotIn('.Enqueue(', DIALOGUE)
        self.assertNotIn('.ClearAllElement(', DIALOGUE)
        self.assertNotIn('.nextDialogue', DIALOGUE)
        self.assertNotIn('currentDialog.plainText', DIALOGUE)

    def test_dialogue_actions_use_native_click_not_story_shortcuts(self):
        self.assertIn('DialogBubbleClickAdvance', DIALOGUE)
        self.assertIn('ExecuteEvents.pointerClickHandler', DIALOGUE)
        self.assertIn('dialogue is still typing', DIALOGUE)
        self.assertIn('ChoiceVisible(choice', DIALOGUE)
        for shortcut in ('ContinueConversation(', 'SelectChoice(', 'SkipTypewriter(', 'ExecuteEndAction('):
            self.assertNotIn(shortcut, DIALOGUE)

    def test_transform_validation_precedes_native_commit(self):
        move = GAME.split('private int Move(', 1)[1].split('private static bool WorldEnabled(', 1)[0]
        self.assertIn('ItemTransform.BuildShape(item, rotation, flipped)', move)
        self.assertLess(move.index('TryInventorySlot('), move.index('ValidatePlacementTransform('))
        self.assertLess(move.index('ValidatePlacementTransform('), move.index('TryAcceptOnce('))
        for check in ('item.MayRemove()', 'item.MaxNumRemove()', 'MayPlayerInsertInto(inventory)'):
            self.assertIn(check, move)
        self.assertNotIn('item.modifiedShape.Set', move)

    def test_configuration_checks_native_permission_and_ownership(self):
        action = GAME.split('case "configure":', 1)[1].split('}', 1)[0]
        self.assertIn('item.MayConfigureSlotItem()', action)
        self.assertIn('item.CanConfigureSlotItem()', action)
        self.assertIn('item.ConfigureSlotItem()', action)
        self.assertLess(action.index('CanConfigureSlotItem()'), action.index('item.ConfigureSlotItem()'))


if __name__ == '__main__':
    unittest.main()
