# ItemDrop：向可见物品堆叠 / 收纳

## 接口与集成要求

新增 `game_drop_item` → operation `drop_item`：

```json
{"snapshot":"...","handle":"source-item","target":"target-item","amount":1}
```

- snapshot/handle/target 为现有 observe 的当前句柄，长度1..128；amount 可省略，默认1，整数1..Int32.MaxValue。
- 不接受 x/y：目标取已观察 item 的命中点，由 GameAdapter 在 mutation 前实时检查 source/target 可见、enabled、无遮挡、target point 第一命中仍是该物品。
- 不接受 rotation/flipped：这是与原生“拖到物品壳上”一致的自动收纳/堆叠。堆叠不改变目标朝向；容器原生找位可能旋转源形状。需要精确方向的调用者应打开库存，使用 `game_move_item` 的显式落点和变换参数。
- 所有执行在 Unity 主线程，已解析尝试后原 observe snapshot 必须由上层消费，错误后重新观察，不自动重试。

Helper：

```csharp
JObject ItemDropAdapter.Drop(GameItemElement source, GameItemElement target, int amount = 1);
```

返回 `moved`、`requested`、`remaining_requested`、`partial`，以及 `route`（`container`、`stack` 或 `container_and_stack`）。上层可附 performed/observe_required。返回只代表该次原生 slot 接收数量，不声称源物品总数或整次交易完成。

## 当前真实程序集核验

- 对象：本机 `Assembly-CSharp.dll`，SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`。
- 定向核验 ItemMouseDragHandler、GameItem、GameInventory、GameGridInventory、SlotMarker、GeneralHelper、GraphUtils、PixelWindow。研究结果只描述接口和顺序，没有复制或分发游戏实现。
- 原生拖拽释放在目标 item 上依次尝试：容器 `MayPlayerInsertInto` + `MayHaveValidInventorySlot` + 最多40次 `TryFindOneValidInventorySlot` / `TryAcceptOnce`；然后 `MayStack` / `GetStackSlot` / `TryAcceptOnce`；最后另有 MayTarget/Target 的使用道具链。
- **本 helper 只实现前两类移动，完全不调用 MayTarget、CanTarget、ItemBehaviourManager.Target 或 GameItem.Target**，不把拖放转换成倒液、修复、消耗、对质、装备等未知语义。没有可用的原生stack/container slot时返回 unavailable，而不是尝试别的动作。

## 权限与图结构

- 拒绝 null、amount<=0、source==target；只沿现有 parent 图检查 source是否为target祖先，拒绝把容器放入自己或后代。parent遍历有visited去重与4096节点上限，不枚举隐藏 childItems。
- source 必须是有效非debug非display-only物品、在inventory中、MayRemove为真且amount不超过MaxNumRemove。原生MayRemove包含移出锁与祖先非拥有容器限制；MaxNumRemove处理最低保留数量、移出数量和父形状修饰。
- pin/拖动/装备待用等交互期间拒绝（不暗中取消玩家当前操作）。
- target必须在inventory中且有效归玩家拥有（包含祖先所有权），并满足原生MayPlayerInsertInto。相比原生服务枪的特殊非拥有例外，本接口有意更保守，不支持向非拥有服务枪/顾客容器投放。
- contentWindow若锁定则拒绝；普通关闭但未锁定的容器可支持，原生拖到壳上收纳本身不要求窗口打开。只看已可见target壳，不把内部内容导出。
- 每个原生候选slot必须引用原source、属于target的后代inventory、不会位于source后代、目的inventory未锁插入且所有权允许；stack必须匹配原source/target和target.parentInventory。

## 状态提交与部分结果

- 只调用原生 `SlotMarker.TryAcceptOnce(amount)`，不手工减少/增加unitCount、不先Expel、不直接调用UncheckedAccept/StackItemUnchecked/AcceptUnchecked。
- TryAcceptOnce内部本来会处理原生stack计数、split、transform与native inventory接收及回调；本接口并非静态扫描意义的沙箱，不承诺第三方Mod委托无副作用。
- 原生容器 `TryFindOneValidInventorySlot` 可能排序内部items、尝试已有堆叠/嵌套容器和自动方向；这是**显式mutation**中的原生行为，不在observe预览中调用。
- 收纳循环最多40遍（与原生一样），每次剩余量减少才继续；容量不足可能部分完成，成功结果明确remaining_requested。后续原生回调若抛错，可能已有部分变更，错误不可被理解成事务回滚。
- 路由保持容器优先再直接stack，但不执行原生拖放最后的use链。容器完全放不下而目标本身可stack时允许stack；不允许静默改变为使用道具。

## 验证

- `tests/ItemDropTests.csproj` 独立引用真实游戏DLL，构建0警告/0错误。
- `dotnet run --project tests/ItemDropTests.csproj -c Release -- '<ManagedDir>'`：16项纯原生parent-graph/参数/slot边界断言通过，包括self、ancestor循环、无关destination、锁定slot、拒绝后不detach source。
- 纯测试绕过Unity构造，仅使用现有GraphNodeStorage parent字段/shape对象，不执行真实Drop，不模拟数量转移算法。
- 全项目对真实DLL构建由Lead集成时复验。尚未通过运行游戏的MCP测试真实堆叠、部分转移、闭容器收纳、嵌套机器规则或存档重载；没有安装/发布或操作游戏。
