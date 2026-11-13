# 装备、物品目标使用与取消（0.3.0）

## 核验范围

对本机真实 `Assembly-CSharp.dll` 定向研究 `ItemSelectHandler`、`ItemContextHandler`、`GameItem`、`ItemBehaviourManager`、`ItemMouseDragHandler`。临时反编译仅在忽略目录 `.inspect/equipment`，不分发原版实现或游戏DLL。

本模块仅提供原生状态机 helper，不控制游戏或桌面，不接受 caller 指定类型/成员，不操作存档文件。必须由主 GameAdapter 在 Unity 主线程以当前 `game_observe` 的 snapshot 和 observed item handles 授权，复查 ready、敏感屏幕、modal、可见性/射线；helper不能替代这些授权检查。

## 真正的原生状态

- `TrySelectItem` 只是临时选中，等待点击目标；不能将其称为已经使用或消费。
- `TryEquipItem` 检查有效所有权和 May/CanSelect，再通过原生 SelectItem/ResetAfter 设置 currentItem、isEquipped，并生成装备光标/高亮。这是持续装备状态，不是消费。
- `GetEquippedItem` 在非equipped时返回null；`currentItem` 同时包含临时选择。helper仅用固定 `ModReflect.Get<GameItem>(handler,"currentItem")` 读取已有引用，不分配ID或补标签。
- `Cancel()` 只取消isEquipped；`EndSelect()` 清理两种状态。adapter分别沿这两个原生入口，保留光标、高亮、tooltip和声效清理，不直接改私有字段。

原生装备更新会每帧读取 `Input.mousePosition` 来绘制光标和hover提示。这不作为MCP目标来源；MCP使用源/目标已经观察到的item引用，绝不假装移动了鼠标或使用当前物理鼠标位置执行目标。

## 集成 API

```csharp
public static class EquipmentAdapter
{
    public static JObject Describe();
    public static GameItem CurrentItem();
    public static JObject Equip(GameItemElement item);
    public static JObject Use(GameItemElement source, GameItemElement target);
    public static JObject Cancel();
}
```

- `Describe` 只读取 handler present/enabled、已有选择引用、IsEquipped/IsDestroyed，返回 selected、equipped、mode、handler_ready等，不执行May/Can委托、不导出隐藏物品名称或ID。
- 主适配器将 `CurrentItem()` 与可见 entries 匹配为 `equipment.source_handle/source_visible`，不输出隐藏item身份。use必须源/目标都可见；本版cancel也采取严格策略：若当前选中源被遮挡或没有可见句柄，先通过原生UI关闭modal再重新观察取消，不以取消工具越过遮挡。idle取消无需虚构源句柄。
- 观察快照应绑定当前ItemSelectHandler、currentItem引用、IsEquipped、unitCount和parentInventory；装备更换/源物品状态改变时拒绝旧动作，不只比较物品名称。
- 所有mutation都检查真实handler active/scene/ready、HasInstance、非拖动物品、非pin阻断、非文字输入；Lead仍必须先做UI modal和两个handle可见性检查。

## 建议 MCP schema（由 Lead dispatcher 集成）

一个 `game_equipment_action` / `equipment_action` 工具，严格oneOf：

```json
{"snapshot":"...","action":"equip","handle":"observed-source-item"}
{"snapshot":"...","action":"use","handle":"equipped-visible-source","target":"observed-target-item"}
{"snapshot":"...","action":"cancel"}
```

不提供自由坐标、world/inventory target或任意行为名称。target与source同一对象表示原生self_activate，不是通用“吃掉/用掉”捷径。

## 使用路径与条件

### Equip

要求没有既有选择（否则先cancel）、源item存在/未destroy/非display-only/debug、有parentInventory、有效归玩家拥有，原生MaySelectSlotItem与CanSelectSlotItem为真，再调用TryEquipItem。返回必须已equipped且currentItem引用相同，否则原生EndSelect清理并报unavailable。

### Use on another item

源必须恰好是当前equipped引用、有效owned且May/CanSelect仍允许；target为可见、在库存中的有效item。

顺序：

1. 原生MayTarget/CanTarget先验证（与原生drag-target入口相同的保守前置条件）；不为了探测在observe调用它们。
2. `ItemBehaviourManager.current.Target(source,target)`：原生行为管线，可能已经修改物品/打开UI。
3. 若源/目标还存在，重新MayTarget/CanTarget，再调用 `source.Target(target)`。原生该方法分发合法目标回调、声音和库存UI刷新；不直接调用onUsed、不手工扣unitCount或改tags。
4. 固定调用原生 `ContinueEquipAfterUse`，由其ShouldExitEquip判断保留装备还是清理，并刷新高亮/光标。不会根据名称模拟结束流程。

原生selection按键入口在Target行为前不先May/Can，而drag-target入口会先检查；adapter采用较保守的drag前置检查，避免不允许目标仍执行行为，可能拒绝少数只靠不受May/Can声明行为的Mod目标，不冒称支持全部Mod。

原生行为/目标回调可能消费物品、改变标签或打开确认窗口；成功仅表示分发，`behaviour_dispatched`、`item_action_dispatched` 分别报告，`consumption_not_inferred=true`。必须observe确认效果，不能把performed当作必定消耗完成。

### Use on self

只有source==target且原生MayActivateSlotItem/CanActivateSlotItem均允许，才走 `ActivateSlotItem`，然后同样ContinueEquipAfterUse。食物或道具是否真正消费取决于它的原生Activate实现；不调用通用FoodHelper或直接减少数量。

### Cancel / 错误

equipped走原生Cancel；临时selection走EndSelect；idle返回cancelled=false，不假装消费。取消后确认currentItem和IsEquipped都清空。

原生执行抛异常时调用EndSelect清理并传播错误；不宣称回滚，已经执行的行为/消费可能永久改变存档，不自动重试。权限检查失败前尚未调用目标执行路径时保持现有装备，供用户重新观察或cancel。

## 本版刻意不支持

- 空白库存/世界IClickable作为equipment target：当前原生selection的目标分支只用RenderHandler.RaycastElement<GameItem>，没有可以泛化的world/inventory目标入口。
- 临时use选择后跨帧虚拟鼠标按键状态，或依靠当前物理鼠标位置触发target。
- 保证所有食物自用、战斗能力、化学液体、特殊工具/Mod行为均完整完成。必须逐物品核验实际回调与确认窗口。
- 原生May/Can、RefreshHighlights可能有标签或其他回调副作用，故只在显式mutation中运行，不作为只读预览/候选目标枚举。

## 验证与待游戏内项目

- helper对真实游戏DLL Release构建：0警告、0错误；不需要新增游戏引用（现有CoreModule、Assembly-CSharp、Newtonsoft.Json即可）。
- `dotnet run --project tests/EquipmentTests.csproj -c Release`：20项纯状态投影/源码路径守卫通过，不调用Unity native。
- 未控制运行游戏、未安装/发布/提交；新equip/use/cancel仍待换版后真实MCP验收：合法equip→observe等待目标→item target/self activate→observe结果→cancel；不拥有/禁用/遮挡/失效source拒绝；已消费source清理；异常后不能盲重试。
