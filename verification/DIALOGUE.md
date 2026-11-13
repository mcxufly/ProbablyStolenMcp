# 原生录音机与店内对话接口（0.2.0）

## 数据与执行边界

此实现只使用已经安装的游戏程序集核验接口，不导出游戏代码或资源。核验对象 Assembly-CSharp SHA256：`725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`。

`StoreRecorder.OnClick` 调用 `RecorderUIManager.Toggle`；录音机 `RefreshUI` 遍历 `PlayerStore.dialogHistory`。`DialogUIManager.UpdateDialogHistory` 向该队列追加去除部分富文本标签的文本，并将记录数量限制到100；`---` 为原生分隔记录。角色名已在原生字符串里，适配器不猜测冒号含义、不拆成可能错误的speaker。

历史在整句开始显示时记录，并不证明打字机已经逐字显示完。保留原生raw_text（包括原生允许的b/u/i标签）并明确记录时机。不会读取nextDialogue、未来分支、未显示选项或重新执行本地化/剧情委托。

队列由PlayerStore构造初始化；原生Recorder有ClearAllElement清空入口。没有证据证明所有历史会跨天/跨存档/进程保留，因此不提供此保证。用户的第2天录音机可能为空，空历史是有效结果，适配器不伪造补全。

所有方法必须在Unity主线程调用。构造传入 `ready = () => InRun() && !SensitiveScreen()` 与 `allowed = Allowed`，沿用主适配层的敏感窗口/配置和可见性过滤；内部再检查HasInstance，避免访问菜单时惰性创建PlayerStore。无后台线程、WebSocket、单独持久日志或Harmony补丁。

## Lead集成签名

```csharp
new DialogueAdapter(Func<bool> ready, Func<Component,bool> allowed);
JObject History(JObject args);
JObject Observe();
JObject Act(string action, JObject args);
void InvalidateActions();
void Reset();
```

一般observe/任意游戏mutation使 `InvalidateActions` 生效；不要对每次observe调用Reset，否则会意外丢失历史分页快照。场景切换、载入/新局或停止服务调用Reset，清空两类快照。每种快照也带scene、30秒TTL，历史另核对PlayerStore对象身份。

## history schema

`game_dialogue_history` → `dialogue_history`：

```json
{"offset":0,"limit":50,"history_snapshot":"可省略"}
```

- offset整数0..Int32.MaxValue；limit整数1..100，默认50；未知字段拒绝。
- 不传history_snapshot：只读复制当前原生队列，保留最多100条且按原顺序，产生新快照。
- 分页传同history_snapshot：读取同一份短期副本，避免新台词加入后页索引漂移。
- 返回history_snapshot、session_id、source=native_recorder、native_capacity=100、total、entries、offset/limit/next_offset、recorded_at_line_start=true。
- entries含record_index、raw_text、separator、text_truncated。record_index仅该history_snapshot中的索引，**不是跨进程稳定seq**；session_id仅此adapter实例随机标识，非存档ID。
- 单条最多4096 Unicode scalar，未配对surrogate替换为U+FFFD；页面预算384KiB，可能少于limit，按next_offset继续。无重订阅/断线无损承诺，超过原生容量的旧记录不可追回。

## 当前对话状态

`game_dialogue_status` → `dialogue_status`，args `{}`。

返回新的dialogue_snapshot、active、typing、waiting_for_manual_advance、can_advance、lines、choices、source、TTL。只覆盖店内DialogUIManager，明确phone_dialogue_supported=false。

- lines来自当前可见且原EventSystem射线未遮挡的TextBubble文本，role为原生bubble种类(customer/player/security)，不是猜测顾客实名。
- raw_text是当前已经开始的一整句，可能比打字机已揭示内容长；返回typing及visible_character_limit，并以text_scope明确不是严格逐字符显示切片。它与原生录音机的整句记录时机一致，不从currentDialog读取全文。
- choices只列实际显示、当前IsInteractable和原Button可用且可raycast的店内选项，含index/text/enabled=true；不执行IsChoiceEnabledAt委托，不枚举未来/隐藏选项。选项文本最多1024scalar、最多100条。
- status无台词时active=false，空lines/choices有效。被modal挡住不声称有可操作对话。

## 原生动作

`game_dialogue_action` → `dialogue_action`：

```json
{"dialogue_snapshot":"...","action":"advance"}
{"dialogue_snapshot":"...","action":"choose","choice_index":0}
```

advance禁止choice_index，choose必须是非负Int32整数；布尔/未知字段拒绝。每次已解析动作尝试都消费dialogue_snapshot，成功返回performed/observe_required。

快照须匹配当前原生Dialogue对象、场景、可见文本/typing/选项状态及30秒TTL。拒绝装备物品状态、typing中（不调用SkipTypewriter）、隐藏/遮挡/禁用控件。advance要求原生当前不是choice，且与原生点击guard一致：isWaitingForManualAdvance / isAutoAdvancePending / isEndPending 至少一个成立；后两者只通过固定游戏成员ModReflect只读读取。通过已挂载DialogBubbleClickAdvance的真实pointerClick执行。choose只通过现有可见DialogChoiceBubble的Button pointerClick执行，不直接调用ContinueConversation、SelectChoice、nextDialogue或endAction。

原生advance点击在非typing时可能继续或结束当前句，取决于原状态；performed只表示分发原生事件不保证剧情/异步动画完成。之后重新status/observe；不能盲目重复动作。打字机期间不会用这个工具加速或跳过。

## 验证

- 对当前真实游戏DLL编译，netstandard2.1，0警告/0错误。
- `tests/DialogueAdapterTests.csproj`：独立net8工具验证原生历史副本投影、顺序、分页、分隔标记、4096scalar截断与UTF16修复。只调用纯helper，不触及Unity native或运行存档。
- 额外引用游戏自带 `Febucci.TextAnimator.Runtime`（Private=false），读取typewriter状态，不捆绑该DLL。
- 本次未操作运行游戏、未安装/发布；新history/status/advance/choose尚待Lead换版后通过MCP实测。历史只读、动作原生入口及schema测试不等同于全部剧情/电话对话验收。
