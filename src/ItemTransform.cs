using System;
using Newtonsoft.Json.Linq;

namespace ProbablyStolenMcp
{
    /// <summary>Copy-only shape transforms; native placement must validate and commit the result.</summary>
    public static class ItemTransform
    {
        public static GridShapeBuilder BuildShape(GameItem item, int? rotation = null, bool? flipped = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (item.modifiedShape == null) throw new InvalidOperationException("item has no modified shape");
            if (rotation.HasValue && (rotation.Value % 90 != 0 || rotation.Value < 0 || rotation.Value > 270))
                throw new ArgumentOutOfRangeException(nameof(rotation), "rotation must be 0, 90, 180, or 270 degrees");
            var shape = new GridShapeBuilder(item.modifiedShape);
            if (rotation.HasValue) shape.SetOrientation(rotation.Value / 90);
            if (flipped.HasValue) shape.SetFlipped(flipped.Value);
            return shape;
        }

        public static GridShapeBuilder BuildShape(GameItemElement item, int? rotation = null, bool? flipped = null)
            => BuildShape((GameItem)item, rotation, flipped);

        /// <summary>Reject slots that force a different requested transform before TryAcceptOnce mutates anything.</summary>
        public static void ValidatePlacementTransform(SlotMarker slot, int? rotation = null, bool? flipped = null)
        {
            if (slot == null || slot.itemGridShape == null) throw new InvalidOperationException("native slot has no placement shape");
            if (rotation.HasValue && (rotation.Value % 90 != 0 || rotation.Value < 0 || rotation.Value > 270))
                throw new ArgumentOutOfRangeException(nameof(rotation), "rotation must be 0, 90, 180, or 270 degrees");
            if ((rotation.HasValue && slot.itemGridShape.orientation != rotation.Value / 90) ||
                (flipped.HasValue && slot.itemGridShape.flipped != flipped.Value))
                throw new InvalidOperationException("destination forces a different item transform");
        }

        public static JObject Describe(GameItem item)
        {
            if (item == null || item.modifiedShape == null) return new JObject();
            var shape = item.modifiedShape;
            return new JObject
            {
                ["cell_x"] = shape.minX, ["cell_y"] = shape.minY,
                ["rotation"] = shape.orientation * 90, ["flipped"] = shape.flipped,
                ["occupied_width_cells"] = shape.globalWidth, ["occupied_height_cells"] = shape.globalHeight
            };
        }

        public static JObject Describe(GameInventory inventory)
        {
            if (inventory == null) return new JObject();
            return new JObject
            {
                ["insert_locked"] = inventory.IsInsertLocked(),
                ["remove_locked"] = inventory.IsRemoveLocked()
            };
        }
    }
}
