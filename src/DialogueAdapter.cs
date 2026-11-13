using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ProbablyStolenMcp
{
    /// <summary>Native recorder/history and visible in-store dialogue. Main thread only.</summary>
    public sealed class DialogueAdapter
    {
        readonly Func<bool> ready;
        readonly Func<Component, bool> allowed;
        readonly string session = Guid.NewGuid().ToString("N");
        string historySnapshot;
        string[] history;
        float historyAt;
        PlayerStore historyOwner;
        int historyScene;
        string actionSnapshot;
        string actionStamp;
        Dialogue actionLine;
        float actionAt;
        int actionScene;
        static readonly Vector2[] Samples = { new Vector2(.5f,.5f), new Vector2(.15f,.15f), new Vector2(.85f,.15f), new Vector2(.15f,.85f), new Vector2(.85f,.85f) };

        public DialogueAdapter(Func<bool> ready, Func<Component, bool> allowed)
        {
            this.ready = ready ?? throw new ArgumentNullException(nameof(ready));
            this.allowed = allowed ?? throw new ArgumentNullException(nameof(allowed));
        }

        void RequireReady()
        {
            if (!ready() || !PlayerStore.HasInstance || !SceneManager.GetActiveScene().isLoaded)
                throw new InvalidOperationException("dialogue unavailable; load a game and close sensitive/modal screens");
        }

        public void Reset()
        {
            InvalidateActions();
            historySnapshot = null;
            history = null;
            historyOwner = null;
        }

        public void InvalidateActions() { actionSnapshot = null; actionStamp = null; actionLine = null; }

        public JObject History(JObject args)
        {
            RequireReady();
            Fields(args, "offset", "limit", "history_snapshot");
            int offset = Integer(args, "offset", 0, 0, int.MaxValue);
            int limit = Integer(args, "limit", 50, 1, 100);
            if (args["history_snapshot"] == null)
            {
                // Existing native recorder queue, not another logging/subscription system.
                var queue = PlayerStore.Instance.dialogHistory;
                history = queue == null ? Array.Empty<string>() : Enumerable.Reverse(Enumerable.Reverse(queue.ToArray()).Take(100)).ToArray();
                historySnapshot = Guid.NewGuid().ToString("N");
                historyAt = Time.realtimeSinceStartup;
                historyScene = SceneManager.GetActiveScene().handle;
                historyOwner = PlayerStore.Instance;
            }
            else if (StringArg(args, "history_snapshot") != historySnapshot || history == null ||
                historyOwner != PlayerStore.Instance || historyScene != SceneManager.GetActiveScene().handle || Time.realtimeSinceStartup - historyAt > 30f)
                throw new InvalidOperationException("history snapshot expired; request a new history page without history_snapshot");
            return BuildHistoryPage(history, historySnapshot, session, offset, limit);
        }

        // Pure bounded projection, tested without running Unity or loading a save.
        internal static JObject BuildHistoryPage(string[] records, string snapshot, string sessionId, int offset, int limit)
        {
            var rows = new JArray();
            int bytes = 0;
            foreach (var pair in records.Select((text, index) => new { text, index }).Skip(offset).Take(limit))
            {
                string text = CleanText(pair.text, 4096);
                var row = new JObject { ["record_index"] = pair.index, ["raw_text"] = text,
                    ["separator"] = pair.text == "---", ["text_truncated"] = text != (pair.text ?? "") };
                int size = Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                if (bytes + size > 384 * 1024) break;
                bytes += size;
                rows.Add(row);
            }
            long next = (long)offset + rows.Count;
            return new JObject { ["history_snapshot"] = snapshot, ["session_id"] = sessionId,
                ["source"] = "native_recorder", ["native_capacity"] = 100, ["total"] = records.Length,
                ["offset"] = offset, ["limit"] = limit, ["entries"] = rows,
                ["next_offset"] = next < records.Length ? new JValue(next) : JValue.CreateNull(),
                ["recorded_at_line_start"] = true, ["snapshot_ttl_seconds"] = 30,
                ["index_scope"] = "record_index belongs only to this history_snapshot; not a persistent sequence" };
        }

        public JObject Observe()
        {
            RequireReady();
            var manager = Manager();
            var state = CurrentState(manager);
            actionSnapshot = Guid.NewGuid().ToString("N");
            actionAt = Time.realtimeSinceStartup;
            actionScene = SceneManager.GetActiveScene().handle;
            actionLine = manager == null ? null : manager.currentDialog;
            actionStamp = state.ToString(Formatting.None);
            state["dialogue_snapshot"] = actionSnapshot;
            state["snapshot_ttl_seconds"] = 30;
            state["source"] = "visible_native_store_dialogue";
            state["phone_dialogue_supported"] = false;
            return state;
        }

        public JObject Act(string action, JObject args)
        {
            RequireReady();
            Fields(args, "action", "dialogue_snapshot", "choice_index");
            if (action != StringArg(args, "action") || (action != "advance" && action != "choose")) throw new ArgumentException("invalid dialogue action");
            if (action == "advance" && args["choice_index"] != null) throw new ArgumentException("advance does not accept choice_index");
            int index = action == "choose" ? Integer(args, "choice_index", -1, 0, int.MaxValue) : -1;
            var manager = Manager();
            var state = CurrentState(manager);
            bool valid = StringArg(args, "dialogue_snapshot") == actionSnapshot && actionSnapshot != null &&
                actionScene == SceneManager.GetActiveScene().handle && Time.realtimeSinceStartup - actionAt <= 30f &&
                manager != null && ReferenceEquals(manager.currentDialog, actionLine) && state.ToString(Formatting.None) == actionStamp;
            InvalidateActions();
            if (!valid) throw new InvalidOperationException("dialogue snapshot expired or state changed; read dialogue status again");
            if (ItemSelectHandler.current != null && ItemSelectHandler.current.IsEquipped) throw new InvalidOperationException("unequip the selected item before dialogue actions");
            if ((bool)state["typing"]) throw new InvalidOperationException("dialogue is still typing; wait instead of skipping");
            if (action == "advance")
            {
                if (!(bool)state["can_advance"]) throw new InvalidOperationException("no visible native dialogue advance is available");
                foreach (var bubble in Bubbles(manager))
                {
                    var click = bubble == null ? null : bubble.GetComponent<DialogBubbleClickAdvance>();
                    if (click != null && Point(click, out var point)) { Click(click.gameObject, point); return Performed(); }
                }
                throw new InvalidOperationException("dialogue bubble became obscured");
            }
            var choice = manager.dialogChoiceBubbles?.FirstOrDefault(c => c != null && c.type == 0 && c.index == index);
            if (!ChoiceVisible(choice, out var choicePoint)) throw new InvalidOperationException("choice is no longer visible and enabled");
            Click(choice.gameObject, choicePoint);
            return Performed();
        }

        static JObject Performed() => new JObject { ["performed"] = true, ["observe_required"] = true };
        DialogUIManager Manager()
        {
            var manager = DialogUIManager.Instance;
            return manager != null && manager.isActiveAndEnabled && manager.gameObject.scene == SceneManager.GetActiveScene() ? manager : null;
        }
        static IEnumerable<TextBubble> Bubbles(DialogUIManager manager)
        {
            if (manager == null) yield break;
            yield return manager.clientBubble; yield return manager.playerBubble; yield return manager.secBubble;
        }

        JObject CurrentState(DialogUIManager manager)
        {
            var lines = new JArray();
            bool typing = false;
            bool bubbleClickable = false;
            foreach (var bubble in Bubbles(manager))
            {
                if (bubble == null || bubble.textfield == null || !allowed(bubble) || !Point(bubble, out var point)) continue;
                bool showing = bubble.typewriter != null && bubble.typewriter.isShowingText;
                typing |= showing;
                var advance = bubble.GetComponent<DialogBubbleClickAdvance>();
                bubbleClickable |= advance != null && Point(advance, out point);
                // The recorder also records the full started line, before the typewriter finishes.
                lines.Add(new JObject { ["role"] = bubble == manager.playerBubble ? "player" : bubble == manager.secBubble ? "security" : "customer",
                    ["raw_text"] = CleanText(bubble.textfield.text, 4096), ["typing"] = showing,
                    ["visible_character_limit"] = bubble.textfield.maxVisibleCharacters,
                    ["text_scope"] = "current_started_line_not_per_character_reveal" });
            }
            var choices = new JArray();
            if (manager?.dialogChoiceBubbles != null)
                foreach (var choice in manager.dialogChoiceBubbles.Take(100))
                    if (ChoiceVisible(choice, out var point)) choices.Add(new JObject { ["index"] = choice.index,
                        ["text"] = CleanText(choice.TMP.text, 1024), ["enabled"] = true });
            bool active = manager != null && manager.currentDialog != null && lines.Count > 0;
            // Match the native non-typing click guard, without dispatching or reading future lines.
            bool advancePending = active && (manager.isWaitingForManualAdvance ||
                ModReflect.Get<bool>(manager, "isAutoAdvancePending") || ModReflect.Get<bool>(manager, "isEndPending"));
            // Do not inspect nextDialogue, execute IsChoiceEnabledAt delegates, or call advance directly.
            return new JObject { ["active"] = active, ["typing"] = typing,
                ["waiting_for_manual_advance"] = active && manager.isWaitingForManualAdvance,
                ["can_advance"] = active && advancePending && !typing && !manager.IsChoice() && choices.Count == 0 && bubbleClickable,
                ["lines"] = lines, ["choices"] = choices };
        }

        bool ChoiceVisible(DialogChoiceBubble choice, out Vector2 point)
        {
            point = Vector2.zero;
            if (choice == null || choice.type != 0 || choice.TMP == null || !choice.IsInteractable() || !allowed(choice)) return false;
            var button = choice.GetComponent<Button>();
            if (button == null || !button.isActiveAndEnabled || !button.IsInteractable()) return false;
            return Point(choice, out point);
        }

        bool Point(Component target, out Vector2 point)
        {
            point = Vector2.zero;
            if (target == null || !allowed(target) || EventSystem.current == null) return false;
            foreach (var graphic in target.GetComponentsInChildren<Graphic>().Take(32))
            {
                if (!allowed(graphic) || !graphic.raycastTarget || graphic.canvasRenderer.cull || graphic.canvasRenderer.GetInheritedAlpha() <= .01f) continue;
                var rect = graphic.rectTransform;
                var canvas = graphic.canvas;
                if (canvas == null || !canvas.isActiveAndEnabled || rect.rect.width <= 0 || rect.rect.height <= 0) continue;
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                foreach (var fraction in Samples)
                {
                    var local = rect.rect.min + Vector2.Scale(rect.rect.size, fraction);
                    point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
                    if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) continue;
                    var hits = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                    if (hits.Count > 0 && hits[0].gameObject != null && (hits[0].gameObject == target.gameObject || hits[0].gameObject.transform.IsChildOf(target.transform))) return true;
                }
            }
            return false;
        }
        static void Click(GameObject target, Vector2 point)
        {
            var data = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left, clickCount = 1 };
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
        }

        internal static string CleanText(string value, int maxScalars)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder();
            int count = 0;
            for (int i = 0; i < value.Length && count < maxScalars; i++, count++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { text.Append(c).Append(value[++i]); }
                else text.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
            return text.ToString();
        }
        static void Fields(JObject args, params string[] fields)
        {
            if (args == null || args.Properties().Any(p => !fields.Contains(p.Name))) throw new ArgumentException("unknown dialogue argument");
        }
        static string StringArg(JObject args, string key)
        {
            if (args[key]?.Type != JTokenType.String) throw new ArgumentException("required snapshot/action string is missing");
            string value = (string)args[key];
            if (value.Length < 1 || value.Length > 128) throw new ArgumentException("snapshot/action length is out of range");
            return value;
        }
        static int Integer(JObject args, string key, int fallback, int min, int max)
        {
            if (args[key] == null) { if (fallback < min) throw new ArgumentException("required choice_index is missing"); return fallback; }
            if (args[key].Type != JTokenType.Integer || !long.TryParse(args[key].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < min || value > max)
                throw new ArgumentException("dialogue integer is outside its valid range");
            return (int)value;
        }
    }
}
