# 验证记录 — 0.1.3 开发预览版

## 0.1.3：世界交互与睡袋采样

0.1.2 MCP 已实测通过库存/原生关闭、开店、叫客、购买、出售、收货和关店。睡眠卡在没有睡袋句柄。离线核验当前原生场景：SleepingBag 用整场景透明 SpriteRenderer，但其 BoxCollider2D 在右下角偏移；旧 renderer 中心采样不在可点区域。修复为现有同 owner Collider2D bounds 有限样点，约束在可见 Renderer 范围、摄像机前方和屏幕内，最终仍使用原 EventSystem 第一命中，不直接调用睡眠业务函数。

新增世界 description/supported_actions/interaction_effect，动态描述仅精确原生 Shutter/Bedroll/Bell 类型且 InRun/HasInstance。随后已通过MCP实际验证睡袋出现、原生结算5信用点、保存提示、夜间报告及第2天545信用点，详情见 [实测记录](LIVE-2026-11-13.md)。这不是退出重启后的存档持久化验收，未扩大到所有玩法。

## 0.1.2：实测驱动的原生 UI 修复

0.1.1 已实际通过 MCP 测到储藏区能打开，但 inventory 和原生按钮/关闭句柄缺失。离线核验真实渲染树：库存和 SelectableElement 的 handler 为零尺寸逻辑根，真实点击几何位于子 Graphic；窗口 X 为固定 topCloseButton 节点而不是 SelectableElement。现按实际可见子节点采样并保持首命中与元素归属检查，只给固定原生 X 节点暴露关闭，不直接调用 Hide。

同时补充运行中 `mod_version`、可见物品公开短描述、库存经射线核实的空白落点；对当前不需要长按的原生 StoreBell 开放正常 click，其他需要长按的路径仍拒绝。本节修复需手动更新并重启后通过 MCP 复验，不把静态构建和源码回归称作真实场景修复已验收。

## 0.1.1 端口配置修复

用户提供的游戏截图已确认 0.1.0 原生配置面板能显示，但端口被错误渲染为滑块；不据此扩大为游玩流程已验收。

核对当前真实 `ModConfigWindow.BuildEntry/ReadWidget/Apply`：带 HasRange 的数字配置生成滑块，不带范围的 int 生成输入框，非整数输入返回 null 并回填旧值。现保留相同 int 类型和 `port` 键，移除 UI range，改在 OnChanged 校验 1024–65535，越界立即恢复上次有效值。原生 Apply 会在回调后把最终 BoxedValue 回填输入框；旧合法端口无须迁移。新增中英文范围/默认值/应用提示。更新到修复 DLL 后需要重启游戏。

已新增端口控件定义回归检查、端口边界断言。尚未在运行游戏中复验修复后的输入框及错误回填；不把源码路径核验与自动测试当作截图实测。

日期：2026-11-13（Asia/Shanghai）。本记录只报告实际完成的验证，不将模拟回调测试称为游戏内实测。

## 版本锚点

- 游戏：Probably Stolen 完整版，Steam App 4348910，安装 build **26525552**。
- Unity `globalgamemanagers`：用 UnityPy 读取 PlayerSettings，`productName = Probably Stolen`、`companyName = Questing Goose Studio`、`bundleVersion = 0.46D`。
- 当前 `Assembly-CSharp.dll` SHA256：`725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`。
- 已定向核验当前 `ModUIManager.VersionMatchesGame` 和 `NormalizeVersion`：与 `Application.version` 的前导数字/点比较。`0.46D` 与本机版本均归一化为 `0.46`，匹配；不是把 XML 能解析当成兼容性验证。
- 当前 DLL 没有新版测试分支 `ModGameVersion` 类型；因此没有按最新在线指南盲填 `1.0.0.14`。本结论不覆盖其他 Steam 构建。

## 已完成的自动验证

### 1. 真实程序集构建

Mod 目标 `netstandard2.1`，仅编译 `src/**/*.cs`。直接引用本机游戏 Assembly-CSharp、Unity、UI、TMP、Newtonsoft.Json，全部 `Private=false`。构建无警告、无错误。

游戏 DLL 仅在本机被引用，不进入发布目录。工具和测试目标为 net8.0，不进入 Mod 程序集。

### 2. Python MCP / 发布布局

`python3 -m unittest discover -s tests -p 'test_*.py' -v`

**49 项通过**：27 项 MCP/网络测试 + 6 项只读实测预检工具测试 + 8 项原生 UI/世界发现路径源码回归守卫 + 8 项清单、本地化、版本一致性、端口控件定义、程序集引用、凭据示例、ZIP 布局与哈希测试。原生 UI 源码守卫不替代运行时验收。

覆盖：三个协议版本与握手、六个工具、stdio 子进程、fake TCP 往返、工具 schema、未知字段、布尔非整数、有限坐标、Unicode、通知不执行动作、重复 JSON 键、非法 JSON、单行大小、分片、10 秒总 deadline 不随分片刷新、断连/超时不自动重试、backend 回显凭据的脱敏、示例不包含有效凭据、Mod 目录无脚本/游戏 DLL。

### 3. C# 协议与 TCP

`dotnet run --project tests/TransportTests.csproj -c Release`

**32 项断言通过**。使用真实 TCP loopback，但游戏回调是纯模拟，不访问 Unity。

覆盖认证失败不执行、令牌范围、固定操作 allowlist、JSON 重复字段/额外 JSON、错误脱敏、不吐堆栈、近期 request_id 防重、分段行、非法多行/流水线、超长请求、幂等停机，以及正好 1 MiB 响应（不含 LF）与超限响应降级。正常调用发完响应后等待客户端先关闭，已验证成功调用后可立即重新绑定独占端口，避免常规配置更新被服务器主动关闭连接的 TIME_WAIT 阻塞。

已完成断线排队回归修复：完整请求先解析，至少下一次 Tick 才执行；执行前拒绝已可见的 FIN 或额外数据。已验证“发完整行后关闭再 Tick”和“解析后关闭再 Tick”不会调用游戏回调。网络断连与正在执行的动作之间仍存在不可消除的竞态，客户端必须保留“可能已执行、勿盲重试”的语义。

### 4. 跨语言集成

`python3 tests/cross_language.py`

**通过**：真实 Python stdio MCP 子进程 → 当前 C# LoopbackServer → 模拟游戏回调 → 标准 MCP 结果。包含初始化、六项工具发现、observe、move_item 参数与返回、不泄露 token。临时子进程在 finally 中关闭。

### 5. 内建扫描器

使用当前游戏的真实 `ModScanner.Scan` 扫描干净发布 Mod 文件夹，`localFolder=false`，共扫描 **145 个方法**。检测能力仅 **network**，与清单一致，**Refused=false、Deprecated=false、Error=none**。

离线运行不能由 `Application.dataPath` 初始化游戏程序集名缓存，所以外部扫描工具将当前真实 Managed 目录中存在的 DLL 名称传入 `allowedReferences`；没有随意豁免第三方引用。此工具只用于静态扫描，不修改游戏信任记录，不批准/启用 Mod，也不等于游戏菜单内审核或运行时验收。

### 6. 适配层参数验证

外部 `AdapterValidation` 工具加载实际构建的 Mod DLL，**36 项断言通过**，对其纯参数辅助函数进行验证：分页范围/布尔类型、数值与下拉选项、未知字段、2048 Unicode 码点边界、避免截断 UTF-16 代理对。它不调用 Unity native，也不验证控件渲染或物品转移结果。

## 源码级核验，不等于运行时验证

详见 [API 核验说明](API-NOTES.md)。已核对原生生命周期、配置、控件、场景交互与库存调用顺序。特别避免在只读观察中调用会创建单例的 `PlayerStore.Instance`、补建标签的 getter 或惰性生成 ID 的方法。

默认关闭、仅 loopback、主线程串行、敏感 UI 屏蔽、快照句柄与可见性复查、容量/所有权的规则执行，都需要下面的游戏内清单确认在真实场景下的行为。

## 已进行的 MCP 游戏内测试与剩余验收

用户已自行安装并载入测试存档。0.1.1 的基础观察/拒绝测试后，0.1.2 通过MCP实际完成储藏区开关、物品移出移回、开店叫客、汽水15买入17卖出、收货及关店（550→535→552）。0.1.3另一次运行中已验证睡袋和动态提示、处理顾客至关店、夜间结算5、出现保存提示和夜间报告，次日第2天资金545（此次从550开始）。完整证据范围见 [MCP 实测记录](LIVE-2026-11-13.md)。当前停在第2天早晨，不继续改变游戏状态；没有公开发布。

尚需在测试存档中验证：

- 模组菜单审核启用、重启、F8 `mod-scan xufly_probably_stolen_mcp`；
- 配置开关/错误令牌/端口冲突、切场景、组件停用/退出关闭 socket；
- observe 中的原生像素 UI、世界物件、文字与实际画面一致；
- 可见对话、开店/关店、交易、睡觉等真实游戏循环能推进；
- 输入通过原生格式/字符限制、下拉/滑块更新正确；
- 遮挡/禁用/过期/复用控件被拒绝，不通过隐藏按钮访问开发或信任层；
- 物品打开、查看、激活、切换及合法移动；
- 错误所有权、锁定、容量不足、目标遮挡不产生非法移动；
- 保存、退出、再载入、禁用/卸载后可继续使用原存档。

## 已知功能限制

- 不支持按住交互、视觉视频/截图、全局鼠标键盘注入。
- 不开放物品选择待用/指定作用目标的使用流程；原生 `TrySelectItem` 只会进入选择状态，尚不足以完整消费，因此没有把它伪装成 `use` API。
- 移动物品只向当前可见容器空白位置放置；不自动堆叠、旋转、翻转或递归寻找空间。
- 主菜单/删除存档/Mod 安全配置由人类管理；调试控制台、刷钱/生成物品等不开放。
- 同步 MCP stdio 一次处理一个请求；执行中不能即时响应 cancellation/ping，网络等待最多 10 秒。取消连接也不能撤销已经执行的原生动作。
- 不承诺当前初版可无人值守游玩所有剧情、小游戏或通关；需要上述真实游戏反馈才能扩大验证范围。
