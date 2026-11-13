"""Unit tests for the live smoke runner; no game connection here."""
import unittest
from unittest.mock import Mock
from live_smoke import inspect_game


def observation(snapshot="fresh", **extra):
    return {"snapshot": snapshot, "mod_version": "0.1.2", "in_run": True, "entries": [], **extra}


class LiveSmokeTests(unittest.TestCase):
    def test_old_dll_needs_restart_without_attempting_gameplay(self):
        tool = Mock(return_value={"in_run": True})
        code, _ = inspect_game(tool)
        self.assertEqual(code, 2)
        self.assertEqual(tool.call_count, 1)

    def test_menu_and_sensitive_screen_need_human_setup(self):
        for result in (observation(in_run=False), observation(blocked_sensitive_screen=True)):
            tool = Mock(return_value=result)
            self.assertEqual(inspect_game(tool)[0], 2)
            self.assertEqual(tool.call_count, 1)

    def test_success_uses_only_readonly_mcp_tools(self):
        tool = Mock(side_effect=[observation("old"), observation(),
                                {"error": {"code": "unavailable"}},
                                {"snapshot": "fresh", "entries": [
                                    {"kind": "inventory", "empty_drop_point": {"x": 1, "y": 2}}]}])
        self.assertEqual(inspect_game(tool)[0], 0)
        self.assertEqual([call.args[0] for call in tool.call_args_list],
                         ["game_observe", "game_observe", "game_inventory", "game_inventory"])

    def test_missing_inventory_reports_failure_not_false_success(self):
        tool = Mock(side_effect=[observation("old"), observation(),
                                {"error": {"code": "unavailable"}}, {"snapshot": "fresh", "entries": []}])
        self.assertEqual(inspect_game(tool)[0], 1)

    def test_stale_snapshot_acceptance_is_failure(self):
        tool = Mock(side_effect=[observation("old"), observation(), {"entries": []}])
        with self.assertRaisesRegex(ValueError, "not rejected"):
            inspect_game(tool)

    def test_errors_never_echo_peer_text_or_secrets(self):
        tool = Mock(return_value={"error": {"code": "unauthorized", "message": "private-secret"}})
        with self.assertRaises(ValueError) as error:
            inspect_game(tool)
        self.assertNotIn("private-secret", str(error.exception))


if __name__ == '__main__':
    unittest.main()
