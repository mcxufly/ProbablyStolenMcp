# GameAdapter：接口核验与边界

## 0.2.0 扩展（待换版实测）

新增 `dialogue_history` / `dialogue_status` / `dialogue_action`，使用原生录音机、独立history/dialogue快照及可见原生点击；详见 [对话专门说明](DIALOGUE.md)。普通observe/mutation使对话动作快照失效，但不丢弃同场景历史分页；场景变化清空两者，历史另核对PlayerStore身份。

`move_item`增加可选 `rotation:0|90|180|270` / `flipped:bool`，绝对值、分别省略保持。由 [ItemTransform](../src/ItemTransform.cs) 复制shape，TryInventorySlot后ValidatePlacementTransform，最后TryAcceptOnce提交；详见 [变换核验](ITEM-TRANSFORM.md)。可见物品返回cell_x/cell_y/rotation/flipped/occupied宽高，库存增加identifier/insert_locked/remove_locked。

`item_action`新增configure：执行时MayConfigureSlotItem与CanConfigureSlotItem均须通过，后者按原生有效所有权判断，再ConfigureSlotItem；不在observe执行May/Can，只根据现有CONFIGURE_TAG和委托提示候选。

上述新增功能只有真实DLL构建、纯算法/协议测试和扫描证据，尚未在运行游戏中加载验收，不扩大旧版实测范围。

## 核验对象与证据

- 核验日期：2026-11-13；对象为本机安装的 `Probably Stolen_Data/Managed/Assembly-CSharp.dll`。
- SHA-256：`725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`。
- 使用任务指定的 ILSpy 11.2.0.10050 定向研究 UI、物品、容器和输入处理类型；没有全量导出工程，没有把反编译实现、游戏 DLL 或资源加入交付。
- [适配器源码](../src/GameAdapter.cs) 对真实游戏程序集编译成功，`netstandard2.1`，0 warnings / 0 errors。0.1.1真实MCP发现库存/窗口漏报后，0.1.2和0.1.3已逐项修复并实测库存、窗口、移动、买卖、关店和睡眠跨日。测试仅通过MCP控制游戏，没有公开发布；未测试项仍明确保留。详见 [实际 MCP 测试记录](LIVE-2026-11-13.md)。
- 参考[官方模组指南](https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.zh-CN.md)，实际 API 和副作用以本机 DLL 核验为准。

编译命令（主工程由 Lead 维护，本人未改其源码/引用配置）：

```sh
dotnet build ProbablyStolenMcp.csproj --no-restore -v minimal
```

需要游戏自带的 `Assembly-CSharp`、`Newtonsoft.Json`、`UnityEngine.CoreModule`、`UnityEngine.UI`、`UnityEngine.UIModule`、`UnityEngine.IMGUIModule`、`UnityEngine.TextRenderingModule`、`Unity.TextMeshPro`、`UnityEngine.Physics2DModule`、`Febucci.TextAnimator.Runtime`。不随 Mod 分发这些 DLL。

## 公共入口与执行约束

```csharp
namespace ProbablyStolenMcp
{
    public sealed class GameAdapter
    {
        public Newtonsoft.Json.Linq.JObject Execute(string operation, JObject arguments);
        public void Reset();
    }
}
```

- **Execute 与 Reset 必须全部在 Unity 主线程调用。** 主线程调度、网络授权、请求/响应大小及错误脱敏由 transport 保证。
- 不接受类型名、成员名、路径、脚本、表达式、控制台命令或任意反射请求。
- 未知 operation、未知字段、参数类型/枚举/范围错误抛 `ArgumentException`；过期句柄、游戏条件、隐藏/禁用/遮挡等失败抛 `InvalidOperationException`。transport 不应向客户端暴露堆栈或任意原生异常信息。
- 每次 observe 使用新的随机 snapshot 和随机 handle；有效期 30 秒，场景切换、Reset、下一次 observe 均失效。操作抵达 `Mutate` 后，包括实时可见性检查失败，均清空整个快照；更早的 JSON/schema/未知 handle 错误不保证消耗快照。客户端在**每次 mutation 尝试之后都重新 observe**，不重试旧 handle。
- 参数的字符串长度按 Unicode scalar values 计数，拒绝未配对 surrogate；UI 输出会替换无效 UTF-16，截断不会切开 surrogate pair。实际游戏输入框的 characterLimit 仍遵循 Unity 自己的 UTF-16 计数。

## 协议

所有对象禁止额外字段；snapshot/handle/target 均为 1..128 字符字符串。

| operation | arguments | 说明 |
|---|---|---|
| `observe` | `{offset?: integer >= 0, limit?: integer 1..100}` | offset 默认 0、limit 默认 50；刷新 snapshot，返回当前画面可见候选条目。每一页 observe 都是新快照，不能拼接旧页句柄。 |
| `inventory` | `{snapshot, offset?: integer >= 0, limit?: integer 1..100}` | 同一 observe 缓存中仍然可见的 item/inventory 分页；不遍历隐藏背包、不刷新 snapshot。默认同上。 |
| `ui_action` | `{snapshot, handle, action?: "click"\|"double_click"\|"scroll_up"\|"scroll_down"}` | 默认 click；double_click 仅 world；scroll 仅 ScrollRect 或原生可滚动库存。 |
| `ui_input` | `{snapshot, handle, value: string (0..2048)}` | 输入框逐字原生编辑；Toggle 精确字符串 `true`/`false`；Dropdown 为现有选项十进制 index；Slider 为 invariant-culture 有限数、位于原范围、满足 whole_numbers。 |
| `item_action` | `{snapshot, handle, action: "open"\|"examine"\|"activate"\|"toggle"\|"configure"}` | 原生物品路径；不暴露不完整的选择待用/目标消费流程。 |
| `move_item` | `{snapshot, handle, target, x: finite number, y: finite number, amount?: integer 1..2147483647, rotation?: 0|90|180|270, flipped?: boolean}` | handle 为源 item，target 为可见 inventory；amount 默认 1；原生规则可能仅移动部分，返回实际 `moved`。 |

`offset` 上限为 Int32.MaxValue。`x/y` 为 viewport 像素，**左下角为原点**，不是浏览器常见左上角坐标；必须在当前屏幕内并实时射线命中目标容器的可见空白区域。不能凭这些参数点击/操作其他目标。

### observe 返回结构

- `snapshot`, `scene`, `mod_version`, `screen: {width,height,origin:"bottom_left"}`。`mod_version` 来自正在游戏进程内执行的 Mod，不把 Python MCP frontend 的 serverInfo 版本当成实际加载 DLL 版本。
- `entries`, `offset`, `limit`, `total`, `next_offset`, `truncated`。
- `in_run`, `blocked_sensitive_screen`, `snapshot_ttl_seconds`, `supported_operations`。
- 最多缓存 4096 个条目；每页最多 100 条，另有约 384 KiB 的条目 JSON 字节预算。文本/标签通常截至 512 UTF-16 单元；输入值至 4096；Dropdown 至 100 个选项并附 `options_total`。字节预算可能令本页少于 limit，按 `next_offset` 继续。
- kind 可为 `button`, `pixel_button`, `window_close`, `world`, `input`, `toggle`, `dropdown`, `slider`, `scroll`, `item`, `inventory`, `text`。text 没有 action handle；window_close 仅支持 `ui_action` 的 `click`，沿该窗口原生 X 按钮事件关闭。
- world 保留原技术名 `label` 以兼容已有脚本，并增加相同的 `technical_name`、玩家可见本地化 `description`（仅原生卷帘门/睡袋/铃铛）、`supported_actions` 与 `interaction_effect: "select"|"activate"`。动作表仅描述当前可分发的原生交互，不承诺业务条件允许或动作已完成；控件即便提示“当前不能关店”，仍可能接受原生 click 后无业务变更。
- 可动作条目包含随机 `handle`、可见采样点 `x/y` 和 UI 的 `bounds`（world 为 null）。bounds 只是几何提示，**不保证矩形内每一点可点击/可放置**；裁切、遮挡和物品占用仍由实时射线及原生规则判断。
- item 附带 `count`, `container`, `examinable`, `owned`, `candidate_actions`, `actions_checked_at_execution`, `inventory_label`, `width_cells`, `height_cells`，以及已存在的公开 `short_description`/`flavor_text`（截断并过滤敏感文本）；父容器有可见空白句柄时附 `inventory_handle`。不调用具有标签/估值/Mod 钩子副作用的 `GetTooltip`。
- `candidate_actions` 只根据已存在的窗口、委托和静态标志提供提示，**不是运行条件已满足的承诺**。observe 不执行任意 May/Can 游戏委托，避免其潜在标签副作用；真正操作时才做完整条件检查。
- inventory 附 `drop_point_required`, `point_must_hit_empty_area`, `scrollable` 和经过当前第一射线命中验证的 `empty_drop_point: {x,y}`，网格库存另外附宽/高格数。该点是当前可见空白表面的候选 drop 坐标，不保证任意形状/数量都能放入；执行时仍需原生容量/所有权/slot 校验。不导出所有 cell、整棵物品图、全部 tags、隐藏价格/剧情/顾客数据。
- input 附 `character_limit`, `character_validation`, `line_type`, `writable`, `custom_validation`，供客户端先选择合适的值。

mutation 成功表示**已向原生控件/路径分发且未抛异常**，不是交易、任务或异步动画已完成；通常返回 `{performed:true, observe_required:true}`。ui_input 另附 `actual_value`, `sanitized`, `control_unavailable`；控件关闭或进入敏感 UI 时 actual/sanitized 为 null，不继续读取。move_item 另附实际转移数量 `moved`，可能少于 amount。

## 已核实的原生路径

### UI / 世界交互

- Unity `Button`、`Toggle`、`InputField`、`TMP_InputField`、`Dropdown`、`TMP_Dropdown`、`Slider` 和 `ScrollRect` 只从当前 loaded scene 中的 active objects 枚举，不用 `Resources.FindObjectsOfTypeAll` 读隐藏 prefab/资源。
- 活跃性、控件 enabled、Selectable.IsInteractable、祖先 CanvasGroup、密码框、屏幕范围和 EventSystem 第一命中结果在 observe 与动作前检查。旧 handle 的标签、输入值、Dropdown 选项/选择等语义标记变化会拒绝，而不是点击复用后的控件。
- 原生像素 UI 不全部使用 Unity Button。已核验 `SelectableElementHandler` 使用 raycast 找到 `SelectableElement`，检查 `isHidden/isDisabled` 后走 Hover → Press → Select/Deselect。适配器复用这条原生状态变化路径，并检查对应 handler 可用；不调用私有按钮回调或重建业务逻辑。
- **0.1.1 漏报根因与修复**：`SelectableElement.handler`、`GameGridInventory.handler` 是逻辑节点，通常没有真实尺寸；`ButtonElement.Validate` 只给内部 buttonNode/textNode 设置 sizeDelta，`GameGridInventory.ValidateBackground` 只给 inner/background 等子节点设置尺寸。原先在 handler.rectTransform 采样因此错过全部点击区域。现在仅在该 native element 当前 active 渲染子树中采样实际 raycast Graphic（最多 256 个，每个 5 点），要求 enabled、alpha/cull、CanvasGroup、敏感过滤与 `FindParentElement()` 精确归属。子物品/子按钮不得向容器提供命中点。bounds 也基于这些实际表面，而非零尺寸根节点。
- `RenderHandler.RaycastElement<T>` 在 EventSystem 射线列表中找到首个 TreeNodeRender，并使用非泛型 `FindParentElement()` 的最近 element 再检查类型；它与 `RaycastFindParentElement<T>` 向上找目标类型的语义不同。本适配器维持最近 element 相同对象的判定，并比原生更保守：如果第一射线命中是普通 Graphic 等遮挡，不会跳过它去点后面的 TreeNode；不会把目标 inventory 的子物品命中当成空白区域。
- **窗口 X 不是 SelectableElement**：`PixelWindow.topCloseButton` 是 element 为 null 的裸 TreeNodeRender，只有自己的原生 mouse 事件。现仅通过固定游戏字段 `ModReflect.Get<TreeNodeRender>(window, "topCloseButton")` 获取这个节点；要求窗口 IsVisible、该节点 active/合法且是第一命中，生成 `window_close`。click 严格分发其 pointer-enter/down/up/click/exit 事件，保留原生 ToFront、closeCallback、Hide 与音效顺序，不直接调用 Hide，不开放任意事件节点。
- 世界 `Interactable` 使用实际 EventSystem 命中、可见 Renderer/摄像机前方、enabled/active、isAvailable/isPaused 过滤。click 走原生 pointer-enter/down/up/exit 路径，controlable 目标先按原选择流程清理旧选择；double_click 在真实选择路径后使用已核验的 `Interactable.OnDoubleClick`。`supported_actions` 只在 WorldEnabled 条件成立时提供：controlable 或有 IClickable 才列 click，有 IDoubleClickable 才列 double_click；执行前再次检查，不能用动作参数越过该表。动态 description 和动作表同时纳入 SemanticStamp，局面变化使旧句柄失效。
- **睡袋漏报根因与修复（已通过MCP复验）**：离线读取当前 level1 的 SleepingBag：SpriteRenderer 使用 `sleeping_bag`，sprite 为 960×540、PPU=100，即整场景大小 9.6×5.4；同对象 BoxCollider2D 的 offset 约 (4.004,-1.967)、size 约 (1.599,1.466)。原来的单 renderer.bounds.center 点不在睡袋点击区域，故即使存在 Renderer 仍漏报，不是长按接口问题（StoreBedroll 只有 IClickable）。现对最多16个同owner合法可见 Renderer、最多16个同owner enabled/active Collider2D，在其已有 bounds 采样5点；候选必须在 renderer XY 范围、摄像机前方、屏幕内，并由原 EventSystem 第一命中确认是同一 Interactable。保留 renderer中心作fallback，不调用 Physics2D.Raycast，不直接调用睡眠业务API，不接受任意坐标，也不放宽遮挡或隐藏对象。
- **动态 world 提示的副作用核验**：仅在 InRun/PlayerStore.HasInstance 就绪后，对运行时类型精确等于 `StoreShutterButton`、`StoreBedroll`、`StoreBell` 的原生组件调用其 IsTooltipEnabled/GetTooltip。前者只读店铺状态/锁标志和 CanEndDay；CanEndDay 遍历现有顾客的 dismissable 与当前顾客蓝图，GetClientBlueprint 仅返回既有字段。睡袋只读 storeState；铃铛只读锁、storeState、IsClientPresent/IsClientDimissable，后两者仅检查当前已有引用与 dismissable。调用只产生与玩家原生提示相同的本地化字符串，不暴露列表、隐藏顾客属性或条件内部细节。其他 TooltipInterface、Mod派生覆写、GameItem.GetTooltip 绝不调用；未知world description为空。
- 定向核验了 `StoreShutterButton`、`StoreBedroll`、`StoreClientCounter`、`DialogUIManager` 等，原路径负责开/关店、睡眠确认和对话等游戏条件。没有直接开放 OpenShutter、奖励/造物、跳日、改钱、剧情触发等业务捷径。**已通过真实0.1.2 MCP实测储藏窗口开关、物品移动往返、开店、叫客、购入汽水15（550→535）、卖出17（→552）及关店；0.1.3另一轮载入后通过MCP完成睡袋交互、原生夜间确认（电费5）、保存提示、夜间报告及第2天545信用点。基础流程已验收，不把两轮资金拼接或宣称全部玩法通过。**
- 若当前场景没有配置可用的世界 EventSystem raycaster，world 条目不会出现；不通过额外物理射线/任意方法调用绕过。

### 输入校验

- 当前 DLL 的 `TMP_InputField.text` setter **不执行**正常逐字符校验，因此不用于填写。
- 适配器只在已授权的可见输入控件上，通过其公开 `ProcessEvent` 送入 SelectAll、Backspace 和逐字符事件，走原生 `onValidateInput`、内建 CharacterValidation/Regex/CustomValidator、characterLimit 等，再提交原控件的 end-edit 通知。不注入全局键盘、不使用剪贴板、不执行字符串命令。
- readonly/密码框、长度上限、不支持的控制字符等在编辑前拒绝；具体游戏 validator 可以过滤/改写字符，`actual_value`/`sanitized` 明确报告最终结果。
- 原生 onValueChanged 可以实时生效；逐字编辑**不是事务**，某个回调拒绝、控件中途关闭或抛异常时可能已部分修改。不会假装回滚，也不会重复运行自定义 validator 去模拟事务。错误后必须 observe。

### 物品与拖放

- 物品对象来自当前可见 `TreeNodeRender.element`，不查找目录工厂、不生成 ID、不遍历隐藏仓库。显示名称读取现有 name/identifier，不调用 `GetDisplayName(true)`，因为它可能间接进入 `GetTagReadonly`。
- 核实 `TagSystem.IsTag` 为现有字典读取；用于 effective ownership/display-only 判断不会补建 tag。observe 不调用 `GetTagReadonly`、`GetUniqueID`、真实属性计算、onUsed、May/Can 委托或会排序真实容器的搜索方法。
- open 使用 `ItemMouseDoubleClickHandler.OpenContentAction`，额外要求窗口未锁、item 有效归玩家拥有、输入 handler 可用；examine 使用其 OpenExamineAction。
- activate/toggle 分别要求原生 May 与 Can 为真，再调用 `ActivateSlotItem` / `ToggleSlotItem`。尊重原生允许的非拥有 document 交互，不强行统一改成所有物品都可用或都不可用。
- 核实原生 `TrySelectItem` 只开启待用选择，目标点击另由输入轮询处理，不能独立完成消费。因此初版明确**不开放 use/指定目标消费**，避免 Agent 进入无法完成或取消的选择态。
- move 先验证源仍可见、目标仍可见且指定点第一命中为该 inventory 空白；再检查 handler/pin/drag 锁、源 `MayRemove()` 与 `MaxNumRemove()`、目标 `GeneralHelper.MayPlayerInsertInto`。`MayRemove` 包含源容器移除锁和祖先非拥有容器限制。
- 以新的 `GridShapeBuilder(item.modifiedShape)` 调用 `TryInventorySlot`，要求 `numTransfer > 0`，再 `TryAcceptOnce(amount)`。由原生 slot 路径处理数量、形状、接收谓词、状态/模块规则、父节点和 add/remove 事件；不调用 UncheckedAccept/AcceptUnchecked、不先手工从源删除。

## 刻意保留的限制 / 安全边界

1. **主菜单无动作**：in_run 需要 `PlayerStore.HasInstance`（不会创建实例）、同一 active loaded scene 上启用的 StoreUIManager/EmporiumEntry、已存在玩家库存和 UI/EventSystem。主菜单可返回非敏感可见文本，但不发 action handles；请由人类先载入测试存档。没有调用会惰性 new PlayerStore 的 `PlayerStore.Instance`。
2. 排除 Mod 配置/信任/审批管理、token/password/bearer/api-key UI、F8 Quantum Console、F9/debug/spawner、主菜单和存档管理等已识别祖先/原生窗口。发现 Mod 管理或敏感原生窗口打开时，整个 observe 帧不输出 UI。通用确认框不能被拿来审批 Mod 信任。过滤有意保守，可能隐藏名称包含敏感关键词的合法游戏内容；这不是面向恶意第三方 Mod 的通用信息流沙箱。
3. 所有 `requiresHold` 像素控件和通常的 `IHoldClickable` world 对象均不暴露；不伪造等待时长，也不调用最终回调绕过长按。唯一明确核验的例外是原生 `StoreBell`：其 `ShouldRequireHold()` 只读取 instantCallNextCustomer 与当前有无顾客，且世界前置检查已确认 `PlayerStore.HasInstance`。只有 IHoldClickable 对象恰好是该 StoreBell、EmporiumOption 已就绪、原生判断当前无需长按时才允许正常 click，执行前再次复查；当前需要长按时仍拒绝。不对任意第三方 IHoldClickable 调用其委托/方法来探测。
4. 没有显式物品目标使用/消费、液体倾倒、装备后目标点击、自动堆叠、递归容器搜索/自动摆放、卸载机器、context-menu 任意函数或全局按键工具。已开放可独立完成的 open/examine/activate/toggle/configure，0.2.0的旋转/翻转通过原生放置路径实现，不是直接修改真实shape。
5. move 只支持当前可见 inventory 的空白 drop；满容器、采样点全部被挡的容器、drop 在其他 item 上的堆叠/容器插入不暴露。原生坐标放置不适用的特殊容器允许返回规则拒绝。
6. 文本可见性依据原生渲染/canvas/cull/alpha/屏幕位置与射线遮挡，几何取样有意保守；未支持世界相机移动、拖动窗口、复杂 IMGUI、任意事件接口或特殊手绘 UI。
7. 0.1.1 MCP实测发现漏报，0.1.2通过窗口开关、库存移动、叫客、一次买卖及关店复验；0.1.3已通过world提示/睡袋交互、夜间结算及次日状态复验。仍需验证真实输入validator、复杂modal/切场景、密码/配置/控制台屏蔽的完整覆盖、物品所有权/锁/模块与满库存/部分转移、退出重启再读档。不得把基础闭环、真实DLL编译或离线纯helper/源码回归检查称为所有功能均验收。
