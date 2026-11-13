"""Source-level guards for the native UI regression found in the live game.

These protect the chosen discovery/security paths, not runtime rendering behavior.
Actual inventory/window-close validation still requires restarting the game DLL.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[1] / 'src/GameAdapter.cs').read_text(encoding='utf-8')


def body(name, following):
    return SOURCE.split(name, 1)[1].split(following, 1)[0]


class NativeUiRegressionTests(unittest.TestCase):
    def test_native_sample_rectangles_use_rendered_children_not_zero_sized_root(self):
        method = body('IEnumerable<RectTransform> HitRects(', 'private static bool TryPoint(')
        self.assertIn('GetComponentsInChildren<Graphic>()', method)
        self.assertIn('Take(MaxNativeGraphics)', method)
        self.assertIn('ReferenceEquals(node.FindParentElement(), native)', method)
        self.assertIn('graphic.raycastTarget', method)
        self.assertIn('graphic.canvasRenderer.cull', method)
        self.assertIn('Allowed(graphic)', method)

    def test_window_close_is_fixed_native_node_not_arbitrary_callback(self):
        method = body('TreeNodeRender WindowCloseNode(', 'private static IEnumerable<RectTransform> HitRects(')
        self.assertIn('ModReflect.Get<TreeNodeRender>(window, "topCloseButton")', method)
        hit = body('bool Hit(Component', 'private static PointerEventData Pointer(')
        self.assertIn('window.IsVisible()', hit)
        self.assertIn('WindowCloseNode(window) == component', hit)
        self.assertIn('hit == component.gameObject', hit)
        click = body('void Click(', 'private static string ReadInputValue(')
        self.assertIn('window_close', click)
        self.assertIn('ExecuteEvents.pointerClickHandler', click)
        self.assertNotIn('.Hide(', click)
        self.assertNotIn('closeCallback', click)

    def test_first_actual_hit_cannot_skip_non_native_blockers(self):
        hit = body('bool Hit(Component', 'private static PointerEventData Pointer(')
        self.assertIn('hits[0].gameObject', hit)
        self.assertIn('var node = hit.GetComponent<TreeNodeRender>()', hit)
        self.assertNotIn('foreach', hit)
        self.assertNotIn('FirstOrDefault', hit)

    def test_observation_has_runtime_version_and_safe_item_descriptions(self):
        self.assertIn('result["mod_version"] = ModEntry.Version', SOURCE)
        self.assertIn('["short_description"]', SOURCE)
        self.assertIn('Clip(item.shortDescription, MaxText)', SOURCE)
        self.assertIn('Clip(item.flavorText, MaxText)', SOURCE)
        item_data = body('JObject ItemData(', 'private static JObject InventoryData(')
        self.assertNotIn('.GetTooltip(', item_data)
        self.assertNotIn('.GetTagReadonly(', SOURCE)

    def test_world_hints_use_exact_audited_native_types_only(self):
        method = body('string WorldDescription(', 'private static bool TryWorldPoint(')
        for native in ('StoreShutterButton', 'StoreBedroll', 'StoreBell'):
            self.assertIn('GetType() == typeof(' + native + ')', method)
        self.assertIn('!InRun() || !PlayerStore.HasInstance', method)
        self.assertNotIn('GetComponent<TooltipInterface>', method)
        self.assertNotIn('StoreDoor', method)
        self.assertIn('["supported_actions"]', SOURCE)
        self.assertIn('["interaction_effect"]', SOURCE)

    def test_world_sample_uses_owned_collider_not_only_sprite_center(self):
        method = body('bool TryWorldPoint(', 'private static TreeNodeRender WindowCloseNode(')
        self.assertIn('GetComponentsInChildren<Collider2D>().Take(16)', method)
        self.assertIn('collider.GetComponentInParent<Interactable>() != world', method)
        self.assertIn('WithinRenderer(renderer, candidate)', method)
        self.assertIn('WorldHit(world, point)', method)
        self.assertIn('target.GetComponentInParent<Interactable>() == world', method)
        self.assertNotIn('Physics2D.Raycast', method)
        self.assertNotIn('Physics.Raycast', method)

    def test_inventory_supplies_verified_empty_drop_point(self):
        self.assertIn('["empty_drop_point"]', SOURCE)
        self.assertIn('point_must_hit_empty_area', SOURCE)
        self.assertIn('if (Hit(component, native, point)) return true', SOURCE)

    def test_conditional_bell_is_only_hold_exception(self):
        method = body('bool WorldEnabled(', 'private static bool TryWorldPoint(')
        self.assertIn('StoreBell', method)
        self.assertIn('ShouldRequireHold()', method)
        self.assertIn('PlayerStore.HasInstance', method)
        self.assertNotIn('StoreSecButton', method)
        self.assertNotIn('StoreDismissButton', method)


if __name__ == '__main__':
    unittest.main()
