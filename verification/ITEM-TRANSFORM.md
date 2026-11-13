# ItemTransform 验证与边界

## 已核实

当前游戏 `GridShapeBuilder` 的 `SetOrientation` 接受内部方向 `0..3`，`SetFlipped` 设置原生镜像标志；`GameItemElement` 的拖动路径通过 `tempShape` 的 `RotCW/RotCCW/GridFlipX` 修改同一语义。`GridShape.ForwardTransform` 与 `globalWidth/globalHeight` 会按方向处理占格。

`ItemTransform.BuildShape` 只复制当前 `modifiedShape`，然后按绝对参数设置：

- `rotation`: 可选整数角度 `0/90/180/270`，对应原生 orientation `0/1/2/3`；是**绝对方向**，不是相对旋转。
- `flipped`: 可选布尔值，是**绝对原生镜像状态**；不是“翻转一次”的动作。
- 参数省略时保留当前形状状态。

它不修改 live `modifiedShape`，不调用 UI 拖动、May/Can 委托、库存搜索、Validate 或事件回调。

## 原生放置顺序

要把变换后的形状用于实际移动，调用方应在已经确认可见 snapshot、源 `MayRemove/MaxNumRemove`、目标所有权和空白落点后：

1. `ItemTransform.BuildShape(item, rotation, flipped)` 复制/变换 shape；
2. 把同一个 `GridShapeBuilder` 传入目标 `GameInventory.TryInventorySlot(item, amount, point, shape, item.modifiedState)`；
3. 要求 `slot.numTransfer > 0`；
4. 调用 `slot.TryAcceptOnce(amount)`。

当前原生 `SlotMarker.TryAcceptOnce` 会把 `itemGridShape` 的位置/方向/镜像通过 `SetTransform` 写入实际物品形状，再调用目标 `UncheckedAccept`。因此变换必须参与 `TryInventorySlot`，不能在通过检查后直接改 live shape。

`GameGridInventory.TryInventorySlot` 会检查网格边界、已有物品重叠、状态修饰、接收修饰、`MaxNumRemove` 和 `MaxNumAdd`。这套路径是可复用的安全落点。`GameSlotInventory` 可能强制其默认方向，因此调用方应比较 slot 返回的 `itemGridShape.orientation/flipped`，若与请求不一致则拒绝，不能声称已应用变换。

## 对外方法

```csharp
GridShapeBuilder ItemTransform.BuildShape(GameItemElement item, int? rotation = null, bool? flipped = null);
void ItemTransform.ValidatePlacementTransform(SlotMarker slot, int? rotation = null, bool? flipped = null);
JObject ItemTransform.Describe(GameItem item);
JObject ItemTransform.Describe(GameInventory inventory);
```

`BuildShape` 另有 GameItem 重载用于纯 helper 测试。`Describe(GameItem)` 只读已存在的 modifiedShape：`cell_x/cell_y/rotation/flipped/occupied_width_cells/occupied_height_cells`。`Describe(GameInventory)` 只读现有 override lock：`insert_locked/remove_locked`，不触发 May/Can 委托、不枚举 childItems。库存 identifier 可能是技术 ID，未擅自添加到输出，Lead 可按可见性策略决定。

`ValidatePlacementTransform` 应在确认 numTransfer>0 后、TryAcceptOnce 前调用；它只比较已返回 slot shape，不修改物品。特殊槽位若重写显式请求的方向/镜像，会提前拒绝而不让移动发生。省略的维度不强制比较，所以旧调用仍保留目标原生槽位行为。

## 自动验证与证据

日期2026-11-13，当前 Assembly-CSharp SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`。定向核验 GridShapeBuilder、GridShape、GameItemElement、ItemMouseDragHandler、GameGridInventory、GameSlotInventory 和 SlotMarker；只说明接口与调用顺序，不分发反编译实现。

- 真实 Mod 项目 `dotnet build -c Release`：0警告、0错误。
- `dotnet run --project tests/ItemTransformTests.csproj -c Release -- '<ManagedDir>'`：**46 项真实游戏纯 shape/copy/transform 断言通过**，覆盖8种方向/镜像组合、原形状不变、local cell不变、全局尺寸、独立省略参数、非法角度、被slot重写方向/镜像拒绝以及readonly描述。
- 测试使用外部 .NET harness，不启动 Unity；未初始化的 GameItemElement 只设 modifiedShape 来调用 pure helper，不进入UI/库存/游戏生命周期。
- **0.2.0已通过真实MCP验证牙膏旋转90度+镜像移动，从3×1变1×3，再恢复0度未镜像及原cell位置**；详见 [实测记录](LIVE-0.2.0.md)。部分堆叠拆分、特殊槽方向、设备形状修饰和存档重载仍未实测，不把此单一物品成功扩大为全部场景。

## 未实现

本文件没有新增 MCP tool，也没有实现自动堆叠、上下文菜单、目标使用、原地旋转或任意容器搜索。MCP schema 扩展应由 Lead 在 `game_move_item` 中增加可选 `rotation` / `flipped`，保持未知字段拒绝；本 helper 本身不负责参数 JSON 解析。