using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ProbablyStolenMcp
{
    /// <summary>Movement-only native drop onto an observed item. Main thread only.</summary>
    public static class ItemDropAdapter
    {
        const int MaxContainerPasses = 40; // Same bound as the native drag-release insertion loop.
        const int MaxAncestorNodes = 4096;

        /// <summary>Caller must resolve fresh visible handles and re-raycast the target item before dispatch.</summary>
        public static JObject Drop(GameItemElement source, GameItemElement target, int amount = 1)
        {
            ValidatePair(source, target, amount);
            if (source.IsDestroyed() || target.IsDestroyed() || source.isDebugMenu || target.isDebugMenu ||
                GeneralHelper.IsDisplayOnly(source) || GeneralHelper.IsDisplayOnly(target))
                throw new InvalidOperationException("drop source or target is not a gameplay item");
            if (ItemPinHelper.BlocksClicks || ItemMouseDragHandler.current == null || !ItemMouseDragHandler.current.isActiveAndEnabled ||
                ItemMouseDragHandler.current.IsDraggingItem || (ItemSelectHandler.current != null &&
                    (ItemSelectHandler.current.IsEquipped || ModReflect.Get<GameItem>(ItemSelectHandler.current, "currentItem") != null)))
                throw new InvalidOperationException("item drop is blocked by another interaction");
            if (source.parentInventory == null || !source.MayRemove() || amount > source.MaxNumRemove())
                throw new InvalidOperationException("requested item removal is not allowed");
            if (target.parentInventory == null || !GeneralHelper.IsItemEffectivelyOwned(target) || !GeneralHelper.MayPlayerInsertInto(target))
                throw new InvalidOperationException("target ownership does not permit item drop");
            if (target.contentWindow != null && target.contentWindow.isLocked)
                throw new InvalidOperationException("target container window is locked");

            int remaining = amount;
            int inserted = 0;
            int stacked = 0;
            // This intentionally excludes the native final MayTarget/Target chain: dropping a
            // bottle/tool must not silently pour, repair, consume, or activate either item.
            if (target.contentWindow != null && target.MayHaveValidInventorySlot(source))
            {
                for (int pass = 0; pass < MaxContainerPasses && remaining > 0; pass++)
                {
                    if (source.parentInventory == null || !source.MayRemove()) break;
                    SlotMarker slot = target.TryFindOneValidInventorySlot(source);
                    if (slot == null || slot.numTransfer < 1) break;
                    ValidateContainerSlot(slot, source, target);
                    int moved = slot.TryAcceptOnce(remaining);
                    if (moved <= 0) break;
                    inserted += moved;
                    remaining -= moved;
                }
            }
            if (remaining > 0 && source.parentInventory != null && source.MayRemove() && target.parentInventory != null &&
                GeneralHelper.IsItemEffectivelyOwned(target) && !target.parentInventory.IsInsertLocked() &&
                GeneralHelper.MayPlayerInsertInto(target.parentInventory) && target.MayStack(source))
            {
                SlotMarker slot = target.GetStackSlot(source);
                if (slot != null && slot.numTransfer > 0)
                {
                    if (!ReferenceEquals(slot.item, source) || !ReferenceEquals(slot.targetItem, target) ||
                        !ReferenceEquals(slot.inventory, target.parentInventory))
                        throw new InvalidOperationException("native stack slot does not match the observed target");
                    stacked = slot.TryAcceptOnce(remaining);
                    remaining -= stacked;
                }
            }
            if (inserted + stacked < 1)
                throw new InvalidOperationException("native stack/container placement is unavailable; other target interactions are not supported");
            return new JObject
            {
                ["moved"] = inserted + stacked,
                ["requested"] = amount,
                ["remaining_requested"] = remaining,
                ["partial"] = remaining > 0,
                ["route"] = inserted > 0 ? (stacked > 0 ? "container_and_stack" : "container") : "stack"
            };
        }

        // Pure relation/bounds checks are independently tested against native graph objects.
        public static void ValidatePair(GameItem source, GameItem target, int amount)
        {
            if (source == null || target == null) throw new ArgumentException("source and target items are required");
            if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount), "amount must be positive");
            if (ReferenceEquals(source, target)) throw new InvalidOperationException("an item cannot be dropped onto itself");
            if (IsAncestor(target, source)) throw new InvalidOperationException("a container cannot be dropped inside itself or its descendants");
        }

        public static void ValidateContainerSlot(SlotMarker slot, GameItem source, GameItem target)
        {
            if (slot == null || !ReferenceEquals(slot.item, source) || slot.inventory == null || slot.itemGridShape == null)
                throw new InvalidOperationException("native container slot is incomplete");
            if (ReferenceEquals(slot.targetItem, source)) throw new InvalidOperationException("native placement would stack the source onto itself");
            if (!IsAncestor(slot.inventory, target) || IsAncestor(slot.inventory, source))
                throw new InvalidOperationException("native destination is outside the target or would create a container cycle");
            if (slot.inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(slot.inventory))
                throw new InvalidOperationException("native destination is locked or not owned");
        }

        static bool IsAncestor(GraphNodeStorage start, GraphNodeStorage ancestor)
        {
            if (start == null || ancestor == null) return false;
            var queue = new Queue<GraphNodeStorage>();
            var seen = new HashSet<GraphNodeStorage>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                GraphNodeStorage current = queue.Dequeue();
                if (current == null || !seen.Add(current)) continue;
                if (ReferenceEquals(current, ancestor)) return true;
                if (seen.Count > MaxAncestorNodes) throw new InvalidOperationException("item parent graph is too large to validate safely");
                // Parent-only traversal does not enumerate or disclose hidden child items.
                var parents = current.parents;
                if (parents != null) foreach (var parent in parents) queue.Enqueue(parent);
            }
            return false;
        }
    }
}
