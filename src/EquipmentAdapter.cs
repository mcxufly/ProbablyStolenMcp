using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyStolenMcp
{
    /// <summary>
    /// Native selected-item state transitions. Call only on Unity's main thread,
    /// after the dispatcher verifies a fresh observation and visible source/target handles.
    /// </summary>
    public static class EquipmentAdapter
    {
        /// <summary>Existing reference only. No May/Can delegates or lazy item IDs.</summary>
        public static GameItem CurrentItem()
        {
            var handler = ItemSelectHandler.current;
            return HandlerPresent(handler) ? ModReflect.Get<GameItem>(handler, "currentItem") : null;
        }

        /// <summary>No hidden item identity is returned; dispatcher may map CurrentItem to a visible handle.</summary>
        public static JObject Describe()
        {
            var handler = ItemSelectHandler.current;
            bool present = HandlerPresent(handler);
            var item = present ? ModReflect.Get<GameItem>(handler, "currentItem") : null;
            return DescribeState(present, present && handler.isActiveAndEnabled, item != null,
                present && handler.IsEquipped, IsDestroyed(item));
        }

        internal static JObject DescribeState(bool present, bool enabled, bool selected, bool equipped, bool destroyed)
        {
            string mode = !selected ? "idle" : equipped ? "equipped_waiting_for_target" : "selected_waiting_for_target";
            return new JObject
            {
                ["handler_present"] = present,
                ["handler_ready"] = present && enabled,
                ["selected"] = selected,
                ["equipped"] = equipped,
                ["selected_item_destroyed"] = destroyed,
                ["mode"] = mode,
                ["supported_target_kind"] = "visible_item_only",
                ["equip_is_not_consumption"] = true
            };
        }

        public static JObject Equip(GameItemElement item)
        {
            var handler = RequireHandler();
            RequireItem(item, "source");
            if (CurrentItem() != null) throw new InvalidOperationException("an item is already selected; cancel before equipping another item");
            if (!GeneralHelper.IsItemEffectivelyOwned(item) || !item.MaySelectSlotItem() || !item.CanSelectSlotItem())
                throw new InvalidOperationException("native rules do not allow equipping this item");
            try
            {
                handler.TryEquipItem(item);
                if (!handler.IsEquipped || !ReferenceEquals(CurrentItem(), item))
                    throw new InvalidOperationException("native equip did not enter the expected state");
                return Result("equip", false, false);
            }
            catch
            {
                // A callback may partially select before failing. Native cleanup, never a retry.
                handler.EndSelect();
                throw;
            }
        }

        public static JObject Use(GameItemElement source, GameItemElement target)
        {
            var handler = RequireHandler();
            RequireItem(source, "source");
            RequireItem(target, "target");
            if (!handler.IsEquipped || !ReferenceEquals(CurrentItem(), source))
                throw new InvalidOperationException("source is not the currently equipped item; observe and equip first");
            if (!GeneralHelper.IsItemEffectivelyOwned(source) || !source.MaySelectSlotItem() || !source.CanSelectSlotItem())
                throw new InvalidOperationException("equipped item is no longer usable");

            bool self = ReferenceEquals(source, target);
            if (self)
            {
                if (!source.MayActivateSlotItem() || !source.CanActivateSlotItem())
                    throw new InvalidOperationException("native rules do not allow self activation");
            }
            else
            {
                if (ItemBehaviourManager.current == null || !ItemBehaviourManager.current.isActiveAndEnabled)
                    throw new InvalidOperationException("native target behaviour manager is unavailable");
                // The native drag-target path gates the behaviour pipeline first; selection's
                // path rechecks after it. Preserve both, never invoke onUsed or consume manually.
                if (!source.MayTarget(target) || !source.CanTarget(target))
                    throw new InvalidOperationException("native rules do not allow this item target");
            }

            bool behaviourDispatched = false;
            bool itemDispatched = false;
            try
            {
                if (self)
                {
                    source.ActivateSlotItem();
                    itemDispatched = true;
                }
                else
                {
                    ItemBehaviourManager.current.Target(source, target);
                    behaviourDispatched = true;
                    // Native behaviours can consume/change the item or open UI. A second
                    // native permission check decides whether item-specific target hooks run.
                    if (!IsDestroyed(source) && !IsDestroyed(target) && source.MayTarget(target) && source.CanTarget(target))
                    {
                        source.Target(target);
                        itemDispatched = true;
                    }
                }
                FinishAfterUse(handler, source);
                return Result(self ? "self_activate" : "target_item", behaviourDispatched, itemDispatched);
            }
            catch
            {
                // No rollback claim: a native behaviour may have already changed the save.
                handler.EndSelect();
                throw;
            }
        }

        public static JObject Cancel()
        {
            var handler = RequireHandler();
            bool selected = CurrentItem() != null;
            if (handler.IsEquipped) handler.Cancel();
            else if (selected) handler.EndSelect();
            if (handler.IsEquipped || CurrentItem() != null)
                throw new InvalidOperationException("native item cancellation did not clear the selection");
            return new JObject
            {
                ["performed"] = selected,
                ["action"] = "cancel",
                ["cancelled"] = selected,
                ["observe_required"] = true,
                ["equipment"] = Describe()
            };
        }

        static void FinishAfterUse(ItemSelectHandler handler, GameItem source)
        {
            if (handler.IsEquipped && ReferenceEquals(CurrentItem(), source))
            {
                // Fixed game-owned member only: the native path decides retain/exit,
                // refreshes highlights/cursor, and calls ShouldExitEquip itself.
                ModReflect.Call(handler, "ContinueEquipAfterUse");
            }
            else if (ReferenceEquals(CurrentItem(), source)) handler.EndSelect();
        }

        static JObject Result(string action, bool behaviourDispatched, bool itemDispatched) => new JObject
        {
            ["performed"] = true,
            ["action"] = action,
            ["behaviour_dispatched"] = behaviourDispatched,
            ["item_action_dispatched"] = itemDispatched,
            ["consumption_not_inferred"] = true,
            ["observe_required"] = true,
            ["equipment"] = Describe()
        };

        static bool HandlerPresent(ItemSelectHandler handler) => handler != null && handler.gameObject.activeInHierarchy &&
            handler.gameObject.scene.IsValid() && handler.gameObject.scene.isLoaded &&
            handler.gameObject.scene == SceneManager.GetActiveScene();

        static ItemSelectHandler RequireHandler()
        {
            var handler = ItemSelectHandler.current;
            if (!PlayerStore.HasInstance || !HandlerPresent(handler) || !handler.isActiveAndEnabled ||
                InputActionManager.current == null || GameMaster.current == null || WindowsHandler.current == null ||
                OverlayHandler.current == null || StoreUIManager.Instance == null)
                throw new InvalidOperationException("native item selection is unavailable");
            if (ItemPinHelper.BlocksClicks || ItemMouseDragHandler.current == null ||
                ItemMouseDragHandler.current.IsDraggingItem || StoreUIManager.Instance.IsTyping())
                throw new InvalidOperationException("item selection is blocked by the current input state");
            return handler;
        }

        static void RequireItem(GameItemElement item, string role)
        {
            if (item == null || item.IsDestroyed() || item.parentInventory == null || GeneralHelper.IsDisplayOnly(item) || item.isDebugMenu)
                throw new InvalidOperationException(role + " item is unavailable");
        }

        static bool IsDestroyed(GameItem item) => item is GameItemElement element && element.IsDestroyed();
    }
}
