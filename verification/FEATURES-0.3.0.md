# 0.3.0 拖放、装备与使用覆盖及验收

## 实现与数据边界

本版沿当前真实游戏DLL的拖放/选择状态机实现，不提供任意鼠标键盘注入、任意反射成员或业务作弊入口。原有9工具保留，新增2个，共11个。

### game_drop_item

参数：`{snapshot,handle,target,amount?}`，目标为已观察的可见物品，数量默认1。不接受x/y、rotation/flipped；需要精确落点和显式变换时用已有game_move_item。

- 主适配器重新校验源和目标句柄均可见/首射线命中，绑定数量、父容器、shape状态，失败的已解析动作也消费快照。
- 源MayRemove/MaxNumRemove、目标和祖先有效所有权、原生接收/容量、插入锁、窗口锁都必须满足。
- 容器插入走原生最多40遍找位与TryAcceptOnce；它可能自动转向/搜索嵌套存储，这是显式mutation，不是只读观察。不能拿它导出隐藏物品。
- 堆叠走MayStack/GetStackSlot/TryAcceptOnce；返回moved/requested/remaining_requested/partial/route，不把部分移动说成全部成功。
- 拒绝source==target和父容器移入其后代的循环关系。只沿parent图验证归属，不枚举隐藏childItems。
- 明确不执行拖放末尾的MayTarget/Target，因此不会把倒液/修复/使用意外当成移动。装备/临时选择期间先cancel，不能混用move/drop与target-use。

详见 [原生拖放核验](ITEM-DROP.md)。

### game_equipment_action

严格参数分支：

```json
{"snapshot":"...","action":"equip","handle":"visible-item"}
{"snapshot":"...","action":"use","handle":"equipped-visible-source","target":"visible-target-item"}
{"snapshot":"...","action":"cancel"}
```

- equip经原生May/CanSelect和有效所有权，用TryEquipItem进入持续装备，不是消费。
- use仅允许当前已装备源物品及可见item目标；源等于目标时只有原生May/CanActivate允许才自激活。异目标走原生行为管线和目标回调，原生ContinueEquipAfterUse决定保留或退出。
- 原生回调可能消耗/改变物品、打开确认UI、触发后果；结果分别报告behaviour_dispatched、item_action_dispatched，consumption_not_inferred=true。performed不代表必定消耗或所有后果已结束。
- cancel经原生Cancel或EndSelect清理装备和临时选择；不确认/关闭任何modal、不撤销已经发生的物品使用。选中源被遮挡时，本版要求先通过原生UI关modal再cancel。
- observe增加equipment状态，只有源可见才给source_handle，不导出隐藏源名称/ID。动作快照绑定选择handler、物品引用、equipped、数量和父容器。
- 装备光标原生raycastTarget=false，不影响首hit，代码不会跳过真实遮挡。MCP目标来自观察句柄，不依赖物理鼠标hover位置。

详见 [原生装备使用核验](EQUIPMENT.md)。

## 本版自动验证与真实状态

- 所有新helper与主适配器对当前真实DLL构建，目标netstandard2.1。
- 协议回归覆盖drop数量/未知字段/禁止坐标变换、equipment三分支oneOf、11tools、通知不执行、错误与超时不重试、token脱敏。
- 原生parent图/边界纯断言16项、equipment状态/源码路径断言20项；另有主适配器集成源码守卫。
- 静态扫描、纯helper、源码守卫及模拟游戏回调不能替代真实Unity使用、消费、堆叠结果。
- 本轮仅通过MCP观察到运行中的旧0.2.0处于blocked_sensitive_screen=true，未操作游戏；**0.3.0新功能全部待用户换版后实测**。

## 自动验证结果

- 真实游戏程序集Release构建：0警告、0错误。
- Python回归69项（含37项bridge、5项本版interaction集成守卫）通过。
- C# transport/protocol 37项断言、adapter纯参数36项、原生历史投影8项、原生shape46项通过。
- equipment状态/源码20项与drop原生parent图/边界16项通过。
- Python stdio→真实C# loopback→模拟游戏回调：11tools及新动作三分支/参数往返通过。
- 原生扫描器213方法：仅network，Refused=false、Deprecated=false、Error=none。

这些均为离线/模拟范围；本版equip/use/cancel与container/stack的真实消费和GUI状态尚无运行中证据。后续提交/tag/校验值使用指定XuFly AI Agent对应密钥签名并验签，不改写旧标签。

## 换版后的测试顺序

1. 关闭游戏，覆盖0.3.0 Mod和外部Python桥，重启/重新连接MCP，手动进入已备份测试存档；关闭Mod/调试设置后确认observe.mod_version=0.3.0。
2. drop：把一件无消耗物品放进可见且有效拥有的空容器→打开确认→移回；再测试相同可堆叠物品合并、满容器/部分数量、锁定/无权/循环目标拒绝。每步重新observe，不盲重试。
3. equipment：选无破坏性工具equip→observe确认equipped/source_handle→cancel→observe确认idle；再选合适目标use，先确认真实玩法后果/是否会耗料，再核对变更和原生保留/退出。
4. 测试自激活只在已知物品的May/CanActivate允许时使用，不能把它当通用吃药/饮食。异常/断连后先读状态，不重复消费。
5. 已选源被弹窗遮挡时拒绝use/cancel；先正常关闭弹窗。测试物品消失/数量变动/父容器或装备切换使旧快照失效。

## 未支持或未覆盖

- 世界对象、空白库存、人物作为equipment目标，当前只适配原生item目标分支。
- 按住/自由轨迹拖动、框选多物品、自由世界坐标点击，不等同于语义drop。
- 保证所有食物、战斗、化学、特殊机器或第三方Mod都能完成消费；原生规则可能拒绝，确认窗口需另行处理。
- 精确定量倒液、完整化学多成分操作、批量自动整理、自由窗口拖动、电话对话、全部剧情与小游戏。
- 新能力的保存退出/重载、卸载和云同步未验收。

实现和未覆盖项按真实证据区分；不以绕过May/Can、UncheckedAccept、改数量/标签或直接onUsed来凑功能。
