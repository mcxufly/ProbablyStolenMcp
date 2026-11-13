using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProbablyStolenMcp
{
    /// <summary>Player-facing adapter. Execute and Reset must run on the Unity main thread.</summary>
    public sealed class GameAdapter
    {
        private const int MaxEntries = 4096;
        private const int MaxText = 512;
        private const int MaxNativeGraphics = 256;
        private static readonly Vector2[] PointFractions = { new Vector2(.5f, .5f), new Vector2(.15f, .15f), new Vector2(.85f, .15f), new Vector2(.15f, .85f), new Vector2(.85f, .85f) };
        private readonly Dictionary<string, Entry> handles = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<Entry> entries = new List<Entry>();
        private string snapshot;
        private float observedAt;
        private int scene;
        private bool truncated;

        private sealed class Entry
        {
            public string Handle;
            public string Kind;
            public string Label;
            public Component Component;
            public object Native;
            public Vector2 Point;
            public JObject Data;
            public string SemanticStamp;
        }

        public void Reset()
        {
            snapshot = null;
            handles.Clear();
            entries.Clear();
        }

        public JObject Execute(string operation, JObject arguments)
        {
            if (arguments == null) throw new ArgumentException("arguments must be an object");
            switch (operation)
            {
                case "observe":
                    Fields(arguments, "offset", "limit");
                    return Observe(PageInt(arguments, "offset", 0, 0, int.MaxValue), PageInt(arguments, "limit", 50, 1, 100));
                case "inventory":
                    Fields(arguments, "snapshot", "offset", "limit");
                    CheckSnapshot(arguments);
                    return Page(entries.Where(e => e.Kind == "item" || e.Kind == "inventory").Where(IsLive).ToList(),
                        PageInt(arguments, "offset", 0, 0, int.MaxValue), PageInt(arguments, "limit", 50, 1, 100));
                case "ui_action":
                    Fields(arguments, "snapshot", "handle", "action");
                    var click = GetEntry(arguments);
                    string action = OptionalString(arguments, "action", "click", 32);
                    if (action != "click" && action != "double_click" && action != "scroll_up" && action != "scroll_down")
                        throw new ArgumentException("unknown UI action");
                    return Mutate(click, () => Click(click, action));
                case "ui_input":
                    Fields(arguments, "snapshot", "handle", "value");
                    var input = GetEntry(arguments);
                    string value = RequiredString(arguments, "value", 0, 2048);
                    JObject inputResult = Mutate(input, () => SetInput(input, value));
                    string actual = ReadInputValue(input.Component);
                    inputResult["actual_value"] = actual == null ? JValue.CreateNull() : new JValue(Clip(actual, 4096));
                    inputResult["sanitized"] = actual == null ? JValue.CreateNull() : new JValue(actual != value);
                    inputResult["control_unavailable"] = actual == null;
                    return inputResult;
                case "item_action":
                    Fields(arguments, "snapshot", "handle", "action");
                    var item = GetEntry(arguments);
                    string itemAction = RequiredString(arguments, "action", 1, 32);
                    if (!new[] { "open", "examine", "activate", "toggle" }.Contains(itemAction))
                        throw new ArgumentException("unknown item action");
                    return Mutate(item, () => ItemAction(item, itemAction));
                case "move_item":
                    Fields(arguments, "snapshot", "handle", "target", "x", "y", "amount");
                    var source = GetEntry(arguments);
                    string targetHandle = RequiredString(arguments, "target", 1, 128);
                    if (!handles.TryGetValue(targetHandle, out var target)) throw new InvalidOperationException("unknown target handle; observe again");
                    float x = Number(arguments, "x");
                    float y = Number(arguments, "y");
                    int amount = PageInt(arguments, "amount", 1, 1, int.MaxValue);
                    int moved = 0;
                    JObject moveResult = Mutate(source, () => moved = Move(source, target, new Vector2(x, y), amount));
                    moveResult["moved"] = moved;
                    return moveResult;
                default: throw new ArgumentException("unsupported operation");
            }
        }

        private JObject Observe(int offset, int limit)
        {
            Reset();
            scene = SceneManager.GetActiveScene().handle;
            truncated = false;
            // No PlayerStore access: menu UI is available before a save exists.
            if (!SensitiveScreen())
            {
                if (InRun())
                {
                var seen = new HashSet<object>();
                foreach (var selectable in Object.FindObjectsOfType<Selectable>().OrderBy(c => c.GetInstanceID()))
                {
                    if (!(selectable is Button || selectable is Toggle || selectable is InputField || selectable is TMP_InputField ||
                        selectable is Dropdown || selectable is TMP_Dropdown || selectable is Slider)) continue;
                    if (!Allowed(selectable) || !selectable.IsInteractable() || !TryPoint(selectable, null, out var point)) continue;
                    string kind = selectable is Button ? "button" : selectable is Toggle ? "toggle" :
                        selectable is InputField || selectable is TMP_InputField ? "input" : selectable is Slider ? "slider" : "dropdown";
                    var data = new JObject();
                    if (selectable is Toggle toggle) data["value"] = toggle.isOn ? "true" : "false";
                    if (selectable is TMP_InputField tmpInput && tmpInput.contentType != TMP_InputField.ContentType.Password)
                    {
                        data["value"] = Clip(tmpInput.text, 4096); data["character_limit"] = tmpInput.characterLimit;
                        data["character_validation"] = tmpInput.characterValidation.ToString(); data["line_type"] = tmpInput.lineType.ToString();
                        data["writable"] = !tmpInput.readOnly; data["custom_validation"] = tmpInput.onValidateInput != null || tmpInput.inputValidator != null;
                    }
                    if (selectable is InputField oldInput && oldInput.contentType != InputField.ContentType.Password)
                    {
                        data["value"] = Clip(oldInput.text, 4096); data["character_limit"] = oldInput.characterLimit;
                        data["character_validation"] = oldInput.characterValidation.ToString(); data["line_type"] = oldInput.lineType.ToString();
                        data["writable"] = !oldInput.readOnly; data["custom_validation"] = oldInput.onValidateInput != null;
                    }
                    if (selectable is TMP_Dropdown tmpDrop) DropdownData(data, tmpDrop.value, tmpDrop.options.Select(o => o.text));
                    if (selectable is Dropdown drop) DropdownData(data, drop.value, drop.options.Select(o => o.text));
                    if (selectable is Slider slider)
                    {
                        data["value"] = slider.value; data["min"] = slider.minValue; data["max"] = slider.maxValue; data["whole_numbers"] = slider.wholeNumbers;
                    }
                    Add(kind, selectable, null, point, Label(selectable), data);
                }
                foreach (var node in Object.FindObjectsOfType<TreeNodeRender>().OrderBy(c => c.GetInstanceID()))
                {
                    if (!Allowed(node)) continue;
                    // A window's X is a bare event-bearing node, not a SelectableElement.
                    var window = node.window;
                    if (window != null && window.IsVisible() && !seen.Contains(window))
                    {
                        seen.Add(window);
                        var close = WindowCloseNode(window);
                        if (close != null && TryPoint(close, window, out var closePoint))
                            Add("window_close", close, window, closePoint, "× " + Clip(window.titleString, MaxText), new JObject { ["action"] = "click" });
                    }
                    object native = node.element;
                    if (native == null || seen.Contains(native)) continue;
                    if (!(native is SelectableElement || native is GameItemElement || native is GameInventory)) continue;
                    seen.Add(native);
                    Component visual = node;
                    if (native is SelectableElement pixel)
                    {
                        if (pixel.isDisabled || pixel.isHidden || pixel.requiresHold || SelectableElementHandler.current == null ||
                            !SelectableElementHandler.current.isActiveAndEnabled) continue;
                        if (!TryPoint(visual, native, out var p)) continue;
                        Add("pixel_button", visual, native, p, PixelLabel(pixel), new JObject());
                    }
                    else if (native is GameItemElement gameItem)
                    {
                        if (gameItem.IsDestroyed() || gameItem.isDebugMenu || GeneralHelper.IsDisplayOnly(gameItem)) continue;
                        if (!TryPoint(visual, native, out var p)) continue;
                        Add("item", visual, native, p, Clip(gameItem.name ?? gameItem.identifier, MaxText), ItemData(gameItem, node));
                    }
                    else if (native is GameInventory)
                    {
                        if (!TryPoint(visual, native, out var p)) continue;
                        var data = InventoryData(native);
                        data["empty_drop_point"] = new JObject { ["x"] = Math.Round(p.x, 2), ["y"] = Math.Round(p.y, 2) };
                        Add("inventory", visual, native, p, Clip(WindowTitle(node), MaxText), data);
                    }
                }
                foreach (var scroll in Object.FindObjectsOfType<ScrollRect>().OrderBy(c => c.GetInstanceID()))
                    if (Allowed(scroll) && TryPoint(scroll, null, out var p)) Add("scroll", scroll, null, p, Label(scroll), new JObject());
                foreach (var world in Object.FindObjectsOfType<Interactable>().OrderBy(c => c.GetInstanceID()))
                {
                    if (!WorldEnabled(world) || !TryWorldPoint(world, out var p)) continue;
                    Add("world", world, null, p, Clip(world.gameObject.name, MaxText), WorldData(world));
                }
                }
                foreach (var text in Object.FindObjectsOfType<TMP_Text>().OrderBy(c => c.GetInstanceID()))
                    if (TextVisible(text) && !string.IsNullOrWhiteSpace(text.text)) Add("text", text, null, Vector2.zero, Clip(text.text, MaxText), null);
                foreach (var text in Object.FindObjectsOfType<Text>().OrderBy(c => c.GetInstanceID()))
                    if (TextVisible(text) && !string.IsNullOrWhiteSpace(text.text)) Add("text", text, null, Vector2.zero, Clip(text.text, MaxText), null);
            }
            foreach (var e in entries.Where(e => e.Native is GameItemElement))
            {
                var parent = ((GameItemElement)e.Native).parentInventory;
                var visibleParent = entries.FirstOrDefault(p => p.Kind == "inventory" && ReferenceEquals(p.Native, parent));
                if (visibleParent != null) e.Data["inventory_handle"] = visibleParent.Handle;
            }
            snapshot = Guid.NewGuid().ToString("N");
            observedAt = Time.realtimeSinceStartup;
            var result = Page(entries, offset, limit);
            result["scene"] = Clip(SceneManager.GetActiveScene().name, 128);
            result["mod_version"] = ModEntry.Version;
            result["screen"] = new JObject { ["width"] = Screen.width, ["height"] = Screen.height, ["origin"] = "bottom_left" };
            result["in_run"] = InRun();
            result["blocked_sensitive_screen"] = SensitiveScreen();
            result["snapshot_ttl_seconds"] = 30;
            result["supported_operations"] = new JArray("observe", "ui_action", "ui_input", "inventory", "item_action", "move_item");
            return result;
        }

        private void Add(string kind, Component component, object native, Vector2 point, string label, JObject data)
        {
            if (entries.Count >= MaxEntries) { truncated = true; return; }
            if (SensitiveWords(label)) return;
            string handle = kind == "text" ? null : Guid.NewGuid().ToString("N");
            var entry = new Entry { Handle = handle, Kind = kind, Component = component, Native = native, Point = point, Label = label, Data = data };
            entry.SemanticStamp = SemanticStamp(entry);
            entries.Add(entry);
            if (handle != null) handles.Add(handle, entry);
        }

        private JObject Page(List<Entry> list, int offset, int limit)
        {
            var array = new JArray();
            int byteCount = 0;
            foreach (var e in list.Skip(offset).Take(limit))
            {
                var row = e.Data == null ? new JObject() : (JObject)e.Data.DeepClone();
                row["kind"] = e.Kind; row["label"] = e.Label;
                if (e.Kind != "text") row["bounds"] = Bounds(e.Component, e.Native);
                if (e.Handle != null)
                {
                    row["handle"] = e.Handle;
                    row["x"] = Math.Round(e.Point.x, 2); row["y"] = Math.Round(e.Point.y, 2);
                }
                int rowBytes = Encoding.UTF8.GetByteCount(row.ToString(Newtonsoft.Json.Formatting.None));
                if (byteCount + rowBytes > 384 * 1024) break;
                byteCount += rowBytes;
                array.Add(row);
            }
            long next = (long)offset + array.Count;
            return new JObject { ["snapshot"] = snapshot, ["entries"] = array, ["offset"] = offset, ["limit"] = limit,
                ["total"] = list.Count, ["next_offset"] = next < list.Count ? new JValue(next) : JValue.CreateNull(), ["truncated"] = truncated };
        }

        private Entry GetEntry(JObject args)
        {
            CheckSnapshot(args);
            string handle = RequiredString(args, "handle", 1, 128);
            if (!handles.TryGetValue(handle, out var entry)) throw new InvalidOperationException("unknown handle; observe again");
            return entry;
        }

        private void CheckSnapshot(JObject args)
        {
            string supplied = RequiredString(args, "snapshot", 1, 128);
            if (snapshot == null || !string.Equals(supplied, snapshot, StringComparison.Ordinal) || scene != SceneManager.GetActiveScene().handle ||
                Time.realtimeSinceStartup - observedAt > 30f || SensitiveScreen())
                throw new InvalidOperationException("snapshot expired or unavailable; observe again");
        }

        private JObject Mutate(Entry entry, Action action)
        {
            // Once a handle resolves, even failed live checks consume the snapshot.
            snapshot = null;
            try
            {
                if (!InRun()) throw new InvalidOperationException("not_in_run: a human must load a game before actions are available");
                if (!IsLive(entry)) throw new InvalidOperationException("control is no longer visible and enabled; observe again");
                action();
            }
            finally { Reset(); }
            return new JObject { ["performed"] = true, ["observe_required"] = true };
        }

        private bool IsLive(Entry e)
        {
            if (!Allowed(e.Component) || e.SemanticStamp != SemanticStamp(e)) return false;
            if (e.Kind == "world") return WorldEnabled(e.Component as Interactable) && WorldHit(e.Component as Interactable, e.Point);
            if (e.Kind == "window_close" && (!(e.Native is PixelWindow window) || !window.IsVisible() ||
                WindowCloseNode(window) != e.Component)) return false;
            if (e.Native is GameItemElement item && (item.IsDestroyed() || item.isDebugMenu || GeneralHelper.IsDisplayOnly(item))) return false;
            if (e.Native is SelectableElement pixel && (pixel.isHidden || pixel.isDisabled || pixel.requiresHold ||
                SelectableElementHandler.current == null || !SelectableElementHandler.current.isActiveAndEnabled)) return false;
            if (e.Component is Selectable s && !s.IsInteractable()) return false;
            return Hit(e.Component, e.Native, e.Point);
        }

        private static string SemanticStamp(Entry e)
        {
            if (e.Kind == "window_close" && e.Native is PixelWindow window) return "close|" + Clip(window.titleString, MaxText);
            if (e.Native is GameItemElement item) return Clip(item.name ?? item.identifier, MaxText);
            if (e.Native is SelectableElement pixel) return PixelLabel(pixel);
            if (e.Native is GameInventory) return WindowTitle((TreeNodeRender)e.Component);
            if (e.Kind == "world")
            {
                var world = (Interactable)e.Component;
                return world.gameObject.name + "|" + world.isControlable + "|" + WorldDescription(world) + "|" +
                    string.Join(",", WorldActions(world));
            }
            string label = Label(e.Component);
            if (e.Component is TMP_InputField tmp) return label + "|" + tmp.text;
            if (e.Component is InputField input) return label + "|" + input.text;
            if (e.Component is Toggle toggle) return label + "|" + toggle.isOn;
            if (e.Component is Slider slider) return label + "|" + slider.value.ToString(CultureInfo.InvariantCulture);
            if (e.Component is TMP_Dropdown tmpDrop)
                return label + "|" + tmpDrop.value + "|" + tmpDrop.options.Count + "|" + string.Join("\u001f", tmpDrop.options.Take(100).Select(o => Clip(o.text, MaxText)));
            if (e.Component is Dropdown drop)
                return label + "|" + drop.value + "|" + drop.options.Count + "|" + string.Join("\u001f", drop.options.Take(100).Select(o => Clip(o.text, MaxText)));
            return label;
        }

        private void Click(Entry e, string action)
        {
            bool scrolling = action == "scroll_up" || action == "scroll_down";
            if (scrolling)
            {
                var delta = new Vector2(0, action == "scroll_up" ? 1 : -1);
                if (e.Native is GameGridScrollableInventory inv) { inv.Scroll(delta); return; }
                if (!(e.Component is ScrollRect)) throw new ArgumentException("handle does not support scrolling");
                var data = Pointer(e.Point); data.scrollDelta = delta;
                ExecuteEvents.Execute(e.Component.gameObject, data, ExecuteEvents.scrollHandler);
                return;
            }
            if (e.Kind == "world")
            {
                var world = (Interactable)e.Component;
                if (!WorldActions(world).Contains(action)) throw new InvalidOperationException("world target does not currently support this action");
                var data = Pointer(e.Point);
                ExecuteEvents.Execute(world.gameObject, data, ExecuteEvents.pointerEnterHandler);
                if (world.isControlable) Interactable.DeselectCurrents();
                ExecuteEvents.Execute(world.gameObject, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(world.gameObject, data, ExecuteEvents.pointerUpHandler);
                // Explicit double-click uses the exact native double-click callback after the real select path.
                if (action == "double_click") world.OnDoubleClick();
                ExecuteEvents.Execute(world.gameObject, data, ExecuteEvents.pointerExitHandler);
                return;
            }
            if (action != "click") throw new ArgumentException("double_click is only available on world handles");
            if (e.Kind == "window_close")
            {
                // Dispatch only the verified X node's own native handlers, never arbitrary event nodes.
                var closePointer = Pointer(e.Point);
                ExecuteEvents.Execute(e.Component.gameObject, closePointer, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(e.Component.gameObject, closePointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(e.Component.gameObject, closePointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(e.Component.gameObject, closePointer, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.Execute(e.Component.gameObject, closePointer, ExecuteEvents.pointerExitHandler);
                return;
            }
            if (e.Native is SelectableElement pixel)
            {
                pixel.Hover(); pixel.Press();
                if (pixel.isSelected) pixel.Deselect(); else pixel.Select();
                pixel.Defocus();
                return;
            }
            if (!(e.Component is Button || e.Component is Toggle || e.Component is Dropdown || e.Component is TMP_Dropdown))
                throw new ArgumentException("handle is not clickable; use ui_input or item_action");
            var pointer = Pointer(e.Point);
            ExecuteEvents.Execute(e.Component.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(e.Component.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(e.Component.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }

        private static string ReadInputValue(Component component)
        {
            if (!Allowed(component) || SensitiveScreen()) return null;
            if (component is TMP_InputField tmp) return tmp.text;
            if (component is InputField input) return input.text;
            if (component is Toggle toggle) return toggle.isOn ? "true" : "false";
            if (component is TMP_Dropdown tmpDrop) return tmpDrop.value.ToString(CultureInfo.InvariantCulture);
            if (component is Dropdown drop) return drop.value.ToString(CultureInfo.InvariantCulture);
            if (component is Slider slider) return slider.value.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static void SetInput(Entry e, string value)
        {
            if (e.Component is TMP_InputField tmp)
            {
                if (tmp.readOnly || tmp.contentType == TMP_InputField.ContentType.Password || tmp.inputType == TMP_InputField.InputType.Password)
                    throw new InvalidOperationException("input is not writable");
                if (tmp.characterLimit > 0 && value.Length > tmp.characterLimit) throw new ArgumentException("value exceeds input character limit");
                ValidateTypedText(value, tmp.lineType == TMP_InputField.LineType.MultiLineNewline);
                // TMP.text bypasses validation. Feed only this control's native edit path instead.
                tmp.ProcessEvent(SelectAllEvent());
                tmp.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace });
                var key = new Event { type = EventType.KeyDown, keyCode = KeyCode.None };
                foreach (char c in value)
                {
                    if (!Allowed(tmp) || !tmp.IsInteractable() || !Hit(tmp, null, e.Point)) throw new InvalidOperationException("input changed or closed during editing");
                    key.character = c;
                    tmp.ProcessEvent(key);
                }
                tmp.ForceLabelUpdate();
                tmp.onEndEdit.Invoke(tmp.text);
                return;
            }
            if (e.Component is InputField input)
            {
                if (input.readOnly || input.contentType == InputField.ContentType.Password || input.inputType == InputField.InputType.Password)
                    throw new InvalidOperationException("input is not writable");
                if (input.characterLimit > 0 && value.Length > input.characterLimit) throw new ArgumentException("value exceeds input character limit");
                ValidateTypedText(value, input.lineType == InputField.LineType.MultiLineNewline);
                input.ProcessEvent(SelectAllEvent());
                input.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace });
                var key = new Event { type = EventType.KeyDown, keyCode = KeyCode.None };
                foreach (char c in value)
                {
                    if (!Allowed(input) || !input.IsInteractable() || !Hit(input, null, e.Point)) throw new InvalidOperationException("input changed or closed during editing");
                    key.character = c;
                    input.ProcessEvent(key);
                }
                input.ForceLabelUpdate();
                input.onEndEdit.Invoke(input.text);
                return;
            }
            if (e.Component is Toggle toggle)
            {
                if (value != "true" && value != "false") throw new ArgumentException("toggle value must be true or false");
                toggle.isOn = value == "true"; return;
            }
            if (e.Component is TMP_Dropdown tmpDrop) { tmpDrop.value = Option(value, tmpDrop.options.Count); return; }
            if (e.Component is Dropdown drop) { drop.value = Option(value, drop.options.Count); return; }
            if (e.Component is Slider slider)
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number) ||
                    number < slider.minValue || number > slider.maxValue || (slider.wholeNumbers && number != Math.Truncate(number)))
                    throw new ArgumentException("slider value must be finite, within range, and respect whole_numbers");
                slider.value = number; return;
            }
            throw new ArgumentException("handle is not an input control");
        }

        private static Event SelectAllEvent() => new Event
        {
            type = EventType.KeyDown,
            keyCode = KeyCode.A,
            modifiers = SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX ? EventModifiers.Command : EventModifiers.Control
        };

        private static void ValidateTypedText(string value, bool allowsNewline)
        {
            if (value.Any(c => char.IsControl(c) && !(allowsNewline && c == '\n')))
                throw new ArgumentException("input contains unsupported control characters");
        }

        private static void ItemAction(Entry e, string action)
        {
            if (!(e.Native is GameItemElement item)) throw new ArgumentException("handle is not an item");
            if (ItemPinHelper.BlocksClicks || (ItemMouseDragHandler.current != null && ItemMouseDragHandler.current.IsDraggingItem) ||
                ItemContextHandler.current == null || !ItemContextHandler.current.isActiveAndEnabled)
                throw new InvalidOperationException("item interaction is currently blocked");
            switch (action)
            {
                case "open":
                    if (item.contentWindow == null || item.contentWindow.isLocked || !GeneralHelper.IsItemEffectivelyOwned(item) || ItemMouseDoubleClickHandler.current == null ||
                        !ItemMouseDoubleClickHandler.current.isActiveAndEnabled)
                        throw new InvalidOperationException("item container cannot be opened");
                    ItemMouseDoubleClickHandler.current.OpenContentAction(item, e.Point); return;
                case "examine":
                    if (item.examineWindow == null || ItemMouseDoubleClickHandler.current == null || !ItemMouseDoubleClickHandler.current.isActiveAndEnabled)
                        throw new InvalidOperationException("item cannot be examined");
                    ItemMouseDoubleClickHandler.current.OpenExamineAction(item, e.Point); return;
                case "activate":
                    if (!item.MayActivateSlotItem() || !item.CanActivateSlotItem()) throw new InvalidOperationException("item cannot be activated");
                    item.ActivateSlotItem(); return;
                case "toggle":
                    if (!item.MayToggleSlotItem() || !item.CanToggleSlotItem()) throw new InvalidOperationException("item cannot be toggled");
                    item.ToggleSlotItem(); return;
            }
        }

        private int Move(Entry source, Entry target, Vector2 point, int amount)
        {
            if (!(source.Native is GameItemElement item) || !(target.Native is GameInventory inventory)) throw new ArgumentException("source must be item; target must be inventory");
            if (!IsLive(target) || !OnScreen(point) || !Hit(target.Component, inventory, point)) throw new InvalidOperationException("destination is no longer visible or point does not hit its empty inventory area");
            if (ItemPinHelper.BlocksClicks || ItemMouseDragHandler.current == null || !ItemMouseDragHandler.current.isActiveAndEnabled ||
                ItemMouseDragHandler.current.IsDraggingItem) throw new InvalidOperationException("item movement is blocked");
            if (item.parentInventory == null || !item.MayRemove() || amount > item.MaxNumRemove()) throw new InvalidOperationException("item removal is not allowed");
            if (!GeneralHelper.MayPlayerInsertInto(inventory)) throw new InvalidOperationException("destination ownership does not permit insertion");
            var shape = new GridShapeBuilder(item.modifiedShape);
            var slot = inventory.TryInventorySlot(item, amount, point, shape, item.modifiedState);
            if (slot == null || slot.numTransfer < 1) throw new InvalidOperationException("native inventory placement rules reject this move");
            int moved = slot.TryAcceptOnce(amount);
            if (moved < 1) throw new InvalidOperationException("native inventory move did not transfer an item");
            return moved;
        }

        private static bool WorldEnabled(Interactable world)
        {
            if (!Allowed(world) || !world.isAvailable || Interactable.isPaused || !PlayerStore.HasInstance) return false;
            var hold = world.GetComponent<IHoldClickable>();
            if (hold == null) return true;
            // Only this audited native control has a state-dependent hold requirement.
            // Never invoke arbitrary mod-provided ShouldRequireHold implementations.
            var bell = world.GetComponent<StoreBell>();
            return ReferenceEquals(hold, bell) && EmporiumOption.Instance != null && !bell.ShouldRequireHold();
        }

        private static JObject WorldData(Interactable world) => new JObject
        {
            // Keep label/technical_name stable for existing clients; description is the native localized hint.
            ["technical_name"] = Clip(world.gameObject.name, MaxText),
            ["description"] = WorldDescription(world),
            ["supported_actions"] = new JArray(WorldActions(world)),
            ["interaction_effect"] = world.isControlable ? "select" : "activate",
            ["selectable"] = world.isControlable
        };

        private static string[] WorldActions(Interactable world)
        {
            if (!WorldEnabled(world)) return Array.Empty<string>();
            var actions = new List<string>();
            if (world.isControlable || world.GetComponent<IClickable>() != null) actions.Add("click");
            if (world.GetComponent<IDoubleClickable>() != null) actions.Add("double_click");
            return actions.ToArray();
        }

        private static string WorldDescription(Interactable world)
        {
            if (world == null || !InRun() || !PlayerStore.HasInstance) return "";
            string description = "";
            // Exact original types only: never dispatch a mod's override or an arbitrary TooltipInterface.
            var shutter = world.GetComponent<StoreShutterButton>();
            var bedroll = world.GetComponent<StoreBedroll>();
            var bell = world.GetComponent<StoreBell>();
            if (shutter != null && shutter.GetType() == typeof(StoreShutterButton) && shutter.IsTooltipEnabled())
                description = shutter.GetTooltip();
            else if (bedroll != null && bedroll.GetType() == typeof(StoreBedroll) && bedroll.IsTooltipEnabled())
                description = bedroll.GetTooltip();
            else if (bell != null && bell.GetType() == typeof(StoreBell) && bell.IsTooltipEnabled())
                description = bell.GetTooltip();
            return SensitiveWords(description) ? "" : Clip(description, MaxText);
        }

        private static bool TryWorldPoint(Interactable world, out Vector2 point)
        {
            point = Vector2.zero;
            var camera = Camera.main;
            if (camera == null) return false;
            // Some native props (notably SleepingBag) use a full-scene transparent sprite:
            // its renderer center misses the offset click collider. Sample only this owner's
            // actual collider bounds within a visible renderer, then use the normal EventSystem.
            foreach (var renderer in world.GetComponentsInChildren<Renderer>().Take(16))
            {
                if (!VisibleWorldRenderer(world, renderer)) continue;
                foreach (var collider in world.GetComponentsInChildren<Collider2D>().Take(16))
                {
                    if (!collider.isActiveAndEnabled || !Allowed(collider) || collider.GetComponentInParent<Interactable>() != world) continue;
                    foreach (var fraction in PointFractions)
                    {
                        var bounds = collider.bounds;
                        var candidate = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, fraction.x),
                            Mathf.Lerp(bounds.min.y, bounds.max.y, fraction.y), renderer.bounds.center.z);
                        if (!WithinRenderer(renderer, candidate)) continue;
                        Vector3 projected = camera.WorldToScreenPoint(candidate);
                        point = projected;
                        if (projected.z > camera.nearClipPlane && WorldHit(world, point)) return true;
                    }
                }
                Vector3 center = camera.WorldToScreenPoint(renderer.bounds.center);
                point = center;
                if (center.z > camera.nearClipPlane && WorldHit(world, point)) return true;
            }
            return false;
        }

        private static bool VisibleWorldRenderer(Interactable world, Renderer renderer) =>
            renderer != null && renderer.enabled && Allowed(renderer) && renderer.GetComponentInParent<Interactable>() == world &&
            (!(renderer is SpriteRenderer sprite) || sprite.color.a > .01f);

        private static bool WithinRenderer(Renderer renderer, Vector3 point)
        {
            var bounds = renderer.bounds;
            return point.x >= bounds.min.x && point.x <= bounds.max.x && point.y >= bounds.min.y && point.y <= bounds.max.y;
        }

        private static bool WorldHit(Interactable world, Vector2 point)
        {
            if (world == null || !OnScreen(point) || EventSystem.current == null || Camera.main == null) return false;
            if (!world.GetComponentsInChildren<Renderer>().Take(16).Any(renderer => VisibleWorldRenderer(world, renderer) &&
                Camera.main.WorldToScreenPoint(renderer.bounds.center).z > Camera.main.nearClipPlane)) return false;
            var hits = Raycast(point);
            // Native EventSystem raycasters account for UI blockers and the actual collider hit order.
            if (hits.Count == 0) return false;
            var target = hits[0].gameObject;
            return target != null && target.GetComponentInParent<Interactable>() == world;
        }

        private static TreeNodeRender WindowCloseNode(PixelWindow window)
        {
            // Literal game type + literal field; never accepts a caller-selected member.
            return window == null ? null : ModReflect.Get<TreeNodeRender>(window, "topCloseButton");
        }

        private static IEnumerable<RectTransform> HitRects(Component component, object native)
        {
            if (native is PixelWindow)
            {
                // The caller is the verified X node, not the whole window.
                if (component.transform is RectTransform closeRect) yield return closeRect;
                yield break;
            }
            if (!(native is PixelElement element))
            {
                if (component.transform is RectTransform rect) yield return rect;
                yield break;
            }
            if (element.handler == null || !Allowed(element.handler)) yield break;
            // Native element handlers are logical roots and commonly have zero size.
            // Only sample their actual active graphics that resolve back to this exact element;
            // nested items and child controls must not donate points to their containing inventory.
            foreach (var graphic in element.handler.GetComponentsInChildren<Graphic>().Take(MaxNativeGraphics))
            {
                if (!graphic.isActiveAndEnabled || !graphic.raycastTarget || graphic.color.a <= .01f || graphic.canvasRenderer.cull ||
                    graphic.canvasRenderer.GetInheritedAlpha() <= .01f || !Allowed(graphic)) continue;
                var node = graphic.GetComponent<TreeNodeRender>();
                if (node != null && ReferenceEquals(node.FindParentElement(), native)) yield return graphic.rectTransform;
            }
        }

        private static bool TryPoint(Component component, object native, out Vector2 point)
        {
            point = Vector2.zero;
            foreach (var rect in HitRects(component, native))
            {
                if (rect.rect.width <= 0 || rect.rect.height <= 0) continue;
                var canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                foreach (var fraction in PointFractions)
                {
                    Vector2 local = rect.rect.min + Vector2.Scale(rect.rect.size, fraction);
                    point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
                    if (Hit(component, native, point)) return true;
                }
            }
            return false;
        }

        private static bool Hit(Component component, object native, Vector2 point)
        {
            if (!Allowed(component) || !OnScreen(point) || EventSystem.current == null) return false;
            var hits = Raycast(point);
            if (hits.Count == 0 || hits[0].gameObject == null) return false;
            var hit = hits[0].gameObject;
            if (native is PixelWindow window)
                return window.IsVisible() && WindowCloseNode(window) == component && hit == component.gameObject;
            if (native != null)
            {
                // Match native RaycastElement's nearest-element ownership. Unlike its search
                // loop, do not skip a non-TreeNode UI blocker in front of the native surface.
                var node = hit.GetComponent<TreeNodeRender>();
                return node != null && Allowed(node) && ReferenceEquals(node.FindParentElement(), native);
            }
            return hit == component.gameObject || hit.transform.IsChildOf(component.transform);
        }

        private static PointerEventData Pointer(Vector2 point)
        {
            if (EventSystem.current == null) throw new InvalidOperationException("no active EventSystem");
            return new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left, clickCount = 1 };
        }

        private static List<RaycastResult> Raycast(Vector2 point)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(Pointer(point), hits);
            return hits;
        }

        private static bool Allowed(Component component)
        {
            if (component == null || !component.gameObject.activeInHierarchy || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded) return false;
            if (component is Behaviour behaviour && !behaviour.isActiveAndEnabled) return false;
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                if (SensitiveWords(t.name)) return false;
                foreach (var mono in t.GetComponents<MonoBehaviour>())
                {
                    if (mono == null) continue;
                    string type = mono.GetType().FullName ?? "";
                    if (type.StartsWith("QFSW.", StringComparison.Ordinal) || SensitiveWords(type)) return false;
                }
                var node = t.GetComponent<TreeNodeRender>();
                if (node != null && ((node.element is GameItem gi && gi.isDebugMenu) || (node.window != null && SensitiveWords(node.window.titleString)))) return false;
                foreach (var group in t.GetComponents<CanvasGroup>())
                    if (!group.interactable || !group.blocksRaycasts || group.alpha <= .01f) return false;
            }
            if (component.GetComponentInParent<TMP_InputField>() is TMP_InputField tmp &&
                (tmp.contentType == TMP_InputField.ContentType.Password || tmp.inputType == TMP_InputField.InputType.Password)) return false;
            if (component.GetComponentInParent<InputField>() is InputField input &&
                (input.contentType == InputField.ContentType.Password || input.inputType == InputField.InputType.Password)) return false;
            return true;
        }

        private static bool InRun()
        {
            var active = SceneManager.GetActiveScene();
            var ui = StoreUIManager.Instance;
            var entry = EmporiumEntry.Instance;
            return PlayerStore.HasInstance && ui != null && entry != null && ui.isActiveAndEnabled && entry.isActiveAndEnabled &&
                ui.gameObject.scene == active && entry.gameObject.scene == active && active.isLoaded && entry.invElement != null &&
                RenderHandler.current != null && EventSystem.current != null;
        }

        private static bool SensitiveScreen()
        {
            // Reject the whole frame, including generic confirmation dialogs, while a trust/config/debug screen is open.
            if (ModUIManager.Instance != null && ModUIManager.Instance.panel != null && ModUIManager.Instance.panel.activeInHierarchy) return true;
            foreach (var frame in Object.FindObjectsOfType<CustomUIWindowFrame>())
                for (Transform t = frame.transform; t != null; t = t.parent)
                    if (t.gameObject.activeInHierarchy && SensitiveWords(t.name)) return true;
            foreach (var node in Object.FindObjectsOfType<TreeNodeRender>())
                if (node.window != null && node.window.IsVisible() && SensitiveWords(node.window.titleString)) return true;
            return false;
        }

        private static bool SensitiveWords(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string s = value.ToLowerInvariant();
            return s.Contains("mod_config") || s.Contains("modconfig") || s.Contains("modui") || s.Contains("modtrust") ||
                s.Contains("modsecurity") || s.Contains("modscan") || s.Contains("modding") || s.Contains("debug") ||
                s.Contains("quantumconsole") || s.Contains("console") || s.Contains("spawner") || s.Contains("token") ||
                s.Contains("password") || s.Contains("api key") || s.Contains("api_key") || s.Contains("bearer") ||
                s.Contains("模组") || s.Contains("调试") || s.Contains("令牌") || s.Contains("信任") || s.Contains("developer mode") ||
                s.Contains("saveuimanager") || s.Contains("saveslot") || s.Contains("load_game") || s.Contains("loadgame") ||
                s.Contains("savegame") || s.Contains("delete_save") || s.Contains("mainmenu") || s.Contains("escapeuimanager") ||
                s.Contains("modpanel") || s.Contains("mod_panel") || s.Contains("openmod") || s.Contains("mods") ||
                s.Contains("save game") || s.Contains("load game") || s.Contains("保存游戏") || s.Contains("载入游戏") ||
                s.Contains("删除存档") || s.Contains("主菜单") || s.Contains("quit");
        }

        private static bool TextVisible(Graphic text)
        {
            if (!Allowed(text) || text.canvas == null || !text.canvas.isActiveAndEnabled || text.canvasRenderer.cull || text.canvasRenderer.GetInheritedAlpha() <= .01f) return false;
            var rect = text.rectTransform;
            Camera camera = text.canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : text.canvas.worldCamera;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            if (!OnScreen(point)) return false;
            if (EventSystem.current == null) return true;
            var hits = Raycast(point);
            if (hits.Count == 0) return true;
            var top = hits[0].gameObject.GetComponent<Graphic>();
            if (top == null || top == text) return true;
            if (top.canvas != null && top.canvas.rootCanvas == text.canvas.rootCanvas) return top.depth <= text.depth;
            return top.canvas == null || top.canvas.sortingOrder < text.canvas.sortingOrder;
        }

        private static JObject ItemData(GameItemElement item, TreeNodeRender node)
        {
            var actions = new JArray();
            // Hints only: invoking game May/Can delegates during observation may have tag side effects.
            if (item.contentWindow != null && !item.contentWindow.isLocked && GeneralHelper.IsItemEffectivelyOwned(item)) actions.Add("open");
            if (item.examineWindow != null) actions.Add("examine");
            if (!item.forceDisableActivate && (item.onActivateSlotItemFunc != null || item.parentInventory?.onActivateSlotItemFunc != null)) actions.Add("activate");
            if (item.onToggleSlotItemFunc != null || item.parentInventory?.onToggleSlotItemFunc != null) actions.Add("toggle");
            return new JObject
            {
                ["count"] = item.unitCount, ["container"] = item.contentWindow != null, ["examinable"] = item.examineWindow != null,
                ["owned"] = GeneralHelper.IsItemEffectivelyOwned(item), ["candidate_actions"] = actions,
                ["short_description"] = SensitiveWords(item.shortDescription) ? "" : Clip(item.shortDescription, MaxText),
                ["flavor_text"] = SensitiveWords(item.flavorText) ? "" : Clip(item.flavorText, MaxText),
                ["actions_checked_at_execution"] = true, ["inventory_label"] = Clip(WindowTitle(node), MaxText),
                ["width_cells"] = item.modifiedShape?.width, ["height_cells"] = item.modifiedShape?.height
            };
        }

        private static JObject InventoryData(object native)
        {
            var data = new JObject { ["drop_point_required"] = true, ["point_must_hit_empty_area"] = true };
            if (native is GameGridInventory grid)
            {
                data["width_cells"] = grid.inventoryShape.width; data["height_cells"] = grid.inventoryShape.height;
            }
            data["scrollable"] = native is GameGridScrollableInventory;
            return data;
        }

        private static JToken Bounds(Component component, object native)
        {
            var points = new List<Vector2>();
            foreach (var rect in HitRects(component, native))
            {
                if (rect.rect.width <= 0 || rect.rect.height <= 0) continue;
                var canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                points.AddRange(corners.Select(p => RectTransformUtility.WorldToScreenPoint(camera, p)));
            }
            if (points.Count == 0) return JValue.CreateNull();
            return new JObject
            {
                ["x_min"] = Math.Round(points.Min(p => p.x), 2), ["y_min"] = Math.Round(points.Min(p => p.y), 2),
                ["x_max"] = Math.Round(points.Max(p => p.x), 2), ["y_max"] = Math.Round(points.Max(p => p.y), 2)
            };
        }

        private static string Label(Component component)
        {
            var text = component.GetComponentsInChildren<TMP_Text>().FirstOrDefault(t => Allowed(t) && !string.IsNullOrWhiteSpace(t.text));
            if (text != null) return Clip(text.text, MaxText);
            var oldText = component.GetComponentsInChildren<Text>().FirstOrDefault(t => Allowed(t) && !string.IsNullOrWhiteSpace(t.text));
            return Clip(oldText != null ? oldText.text : component.gameObject.name, MaxText);
        }

        private static string PixelLabel(SelectableElement pixel)
        {
            if (pixel is ButtonElement button && !string.IsNullOrEmpty(button.text)) return Clip(button.text, MaxText);
            return Label(pixel.handler);
        }

        private static string WindowTitle(TreeNodeRender node)
        {
            for (Transform t = node.transform; t != null; t = t.parent)
            {
                var parent = t.GetComponent<TreeNodeRender>();
                if (parent != null && parent.window != null) return parent.window.titleString ?? "inventory";
            }
            return "inventory";
        }

        private static void DropdownData(JObject data, int selected, IEnumerable<string> options)
        {
            var all = options.ToList();
            data["value"] = selected.ToString(CultureInfo.InvariantCulture);
            data["options"] = new JArray(all.Take(100).Select((s, i) => new JObject { ["index"] = i, ["label"] = SensitiveWords(s) ? "[restricted]" : Clip(s, MaxText) }));
            data["options_total"] = all.Count;
        }

        private static int Option(string value, int count)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int option) || option < 0 || option >= count)
                throw new ArgumentException("dropdown value must be an existing option index");
            return option;
        }

        private static bool OnScreen(Vector2 point) => point.x >= 0 && point.y >= 0 && point.x < Screen.width && point.y < Screen.height;
        private static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            int length = Math.Min(value.Length, max);
            if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
            // UI strings are not necessarily well-formed UTF-16 (e.g. during composition).
            var clean = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < length && char.IsLowSurrogate(value[i + 1])) { clean.Append(c); clean.Append(value[++i]); }
                    else clean.Append('\uFFFD');
                }
                else clean.Append(char.IsLowSurrogate(c) ? '\uFFFD' : c);
            }
            return clean.ToString();
        }

        private static int ScalarLength(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw new ArgumentException("invalid Unicode string");
                    i++;
                }
                else if (char.IsLowSurrogate(value[i])) throw new ArgumentException("invalid Unicode string");
            }
            return count;
        }

        private static void Fields(JObject args, params string[] names)
        {
            foreach (var property in args.Properties()) if (!names.Contains(property.Name)) throw new ArgumentException("unknown argument");
        }

        private static string RequiredString(JObject args, string key, int min, int max)
        {
            var value = args[key];
            if (value == null || value.Type != JTokenType.String) throw new ArgumentException(key + " must be a string");
            string text = (string)value;
            if (ScalarLength(text) < min || ScalarLength(text) > max) throw new ArgumentException(key + " length is out of range");
            return text;
        }

        private static string OptionalString(JObject args, string key, string fallback, int max) => args[key] == null ? fallback : RequiredString(args, key, 1, max);

        private static int PageInt(JObject args, string key, int fallback, int min, int max)
        {
            var token = args[key];
            if (token == null) return fallback;
            if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < min || value > max)
                throw new ArgumentException(key + " must be an integer in range");
            return (int)value;
        }

        private static float Number(JObject args, string key)
        {
            var token = args[key];
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) ||
                !float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException(key + " must be a finite number");
            return value;
        }
    }
}
