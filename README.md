# Probably Stolen MCP / 深空当铺 Agent 接口

**0.2.0，本地开发预览版。** 为《深空当铺：可能是偷的》（Steam App 4348910）提供可由大模型 Agent 调用的游戏观察与操作工具。使用游戏原生 Mod 加载器；Agent 调用标准 **MCP stdio**，由 Python 桥转发到游戏内的本机 TCP 服务。

> 自动测试不等于游戏实测。已通过真实 MCP 验证库存/窗口开关、物品移动往返、开店叫客、15买入17卖出及关店；0.1.3 又验证了睡袋、夜间服务结算和进入次日（第1天550→扣电费5→第2天545）。买卖与跨日分属换版前后的两次运行，不冒称同一笔利润跨重启保留。完整范围见 [MCP 实测记录](verification/LIVE-2026-11-13.md)。基础经营流程已跑通，但并不代表全部玩法或无人值守通关已支持。请使用备份/测试存档；原生操作会真实花钱、消耗物品并改变存档。

## 0.2.0：原生录音机、对话控制与库存变换

本版已对真实程序集构建和自动验证，且用户换版后已通过真实 MCP 验收原生非空历史/稳定分页与录音机窗口对照、typing状态和拒绝、原生advance、快照失效，以及牙膏90度+镜像再恢复原状。**本轮未遇到回复选择或configure物品，这两项仍待实测**；特殊槽/变换读档等也未覆盖。详见 [0.2.0实测记录](verification/LIVE-0.2.0.md)。

- `game_dialogue_history`：直接复制游戏录音机 `PlayerStore.dialogHistory`，按原生顺序读取最近最多100条，不另建日志、不读取未来台词；分页使用独立 `history_snapshot`，30秒内读同一副本。记录包含角色名的原文，不用冒号猜拆说话者。
- `game_dialogue_status`：返回当前可见店内台词、打字机状态、原生待继续状态、已显示且可点击的选项和独立 `dialogue_snapshot`。
- `game_dialogue_action`：使用上述对话快照继续或选回复，只派发原生点击，拒绝打字机未结束、隐藏/遮挡/禁用选项，不直接跳剧情。暂不支持电话对话。
- `game_move_item` 新增 `rotation` / `flipped`：绝对逆时针角度0/90/180/270和绝对原生镜像标志；分别省略则保持原状态。先复制shape、经原生容量/形状/所有权规则验证，再提交；特殊槽位若强制其他方向，会在移动前拒绝显式变换。
- 物品返回 `cell_x/cell_y/rotation/flipped/occupied_width_cells/occupied_height_cells`；可见库存增加 `identifier/insert_locked/remove_locked`，不遍历隐藏内容。
- `game_item_action` 增加 `configure`，遵循原生 May/CanConfigure 和有效所有权检查，打开带设置的物品/机器。

三个快照不可混用：`snapshot`用于普通UI/物品，`history_snapshot`用于稳定历史分页，`dialogue_snapshot`用于对话动作。通用观察/动作会使旧对话动作快照失效；历史分页不因普通观察失效，但受场景、PlayerStore身份和30秒TTL约束。

录音机在整句开始显示时记入全文，不代表逐字动画已播完。历史超过100条的部分无法补回，跨天/跨进程保存不作保证；`record_index`仅是该历史快照内的索引，不是全局事件序号。详见 [对话接口](verification/DIALOGUE.md) 和 [旋转翻转说明](verification/ITEM-TRANSFORM.md)。本版**没有 WebSocket**，直接服务现有 stdio MCP 客户端。

## 0.1.3：睡袋与世界交互信息

- 修复睡袋使用全场景透明 sprite、实际点击 collider 偏移导致的中心采样漏报。只采样同一可见物件的现有 Collider2D，仍要求原生 EventSystem 首命中一致，不绕过遮挡或直接跳日。
- world 条目保留原 `label`，新增 `technical_name`、`description`、`supported_actions`、`interaction_effect`。
- 动态描述仅来自已核验的原生卷帘门、睡袋、铃铛精确类型；不调用任意 Mod tooltip。旧句柄的描述/动作变化也会触发过期拒绝。
- **睡袋发现、动态提示、原生结算确认、保存提示及第2天状态均已通过0.1.3的MCP实测。** 尚未验证全部机器/输入框/长按/物品使用/剧情；详见限制和记录。当前说明是 v0.1.3 实现标签后的实测文档补充，DLL 行为不变、不覆盖旧标签。

## 0.1.2：根据 MCP 实测修复

- 库存与像素控件改从实际可见渲染子节点采样，不再把零尺寸逻辑根当成点击区域；保持遮挡和原生元素归属校验。
- 识别原生窗口固定的右上角关闭节点，新增 `window_close` 句柄，仍用 `game_ui_action(click)`，不直接 Hide 或开放任意回调。
- 库存附带经当前射线核实的 `empty_drop_point`，物品附带已有 `short_description` / `flavor_text`，不生成带副作用的完整 tooltip。
- observe 返回实际游戏 DLL 的 `mod_version`，便于区分 Python 服务版本与未重启的旧 Mod。
- 修复铃铛被无条件长按过滤的情况：只在原生 StoreBell 当前明确不需要长按时提供点击；需要长按时仍不绕过。

**库存命中、储藏区关闭、物品移动、铃铛和一买一卖流程已通过运行中 0.1.2 的 MCP 复验；睡眠跨日随后由0.1.3补齐并实测。** 不支持运行时热替换；更新时关闭游戏、覆盖本模组，再重启载入测试存档，合法端口和令牌配置保留。

## 设计与边界

```text
大模型 Agent / MCP 客户端
  └─ 标准 JSON-RPC / stdio → Python bridge/server.py（标准库，无 pip 依赖）
       └─ 带访问令牌的 TCP JSON 行 → 127.0.0.1:17653
            └─ 原生 Mod 自己的 MonoBehaviour.Update → 可见控件 / 原生物品操作
```

- 网络只绑定 **IPv4 127.0.0.1**。不监听局域网、不连接远程服务器、不收集 Steam 身份。
- 默认关闭。用户显式配置私密令牌并开启后才能使用；缺失或非法令牌不启动监听。
- 操作在 Unity **主线程**按帧串行执行。不启动 Mod 私有线程、不使用 Harmony、不开放控制台/任意反射/任意代码执行。
- 观察获得短期 `snapshot` 和不透明 `handle`；动作必须使用最近的快照，并再次检查可见性、可交互性和原生规则。尝试状态变更后需要重新观察。
- 不是全知数据导出：不提供隐藏顾客属性、未来剧情、全局对象树、存档任意读写、生成物品或修改金钱。
- 仅暴露已进入游戏后的交互；主菜单、新建/载入/删除存档和 Mod 信任/令牌设置由人类管理。开发工具不供 Agent 操作。
- 这是语义/控件接口，不是屏幕视频、鼠标键盘注入或通用视觉 Agent。复杂的按住交互、目标使用、液体倾倒及特殊小游戏等可能仍需人类操作；详情见 [API 核验说明](verification/API-NOTES.md)。

## MCP 工具

| 工具 | 参数 | 用途 |
|---|---|---|
| `game_observe` | `offset?`, `limit?` | 读取当前可见局面、文本、原生 UI/场景控件，生成新 snapshot 与句柄；分页默认值由游戏适配层给出，limit 1–100 |
| `game_ui_action` | `snapshot`, `handle`, `action?` | `click`（默认）、`double_click`、`scroll_up`、`scroll_down`；只允许该句柄支持的动作 |
| `game_ui_input` | `snapshot`, `handle`, `value` | 填写文本；开关传字符串 `true`/`false`，下拉框传索引字符串，滑块传有限数字字符串；最长 2048 字符 |
| `game_inventory` | `snapshot`, `offset?`, `limit?` | 展开该快照已发现的可见物品/容器；不刷新 snapshot |
| `game_item_action` | `snapshot`, `handle`, `action` | 原生 `open`、`examine`、`activate`、`toggle`、`configure`；不开放无法完整收尾的选择待用/指定作用目标流程 |
| `game_move_item` | `snapshot`, `handle`, `target`, `x`, `y`, `amount?`, `rotation?`, `flipped?` | 原生移动与旋转/翻转，需当前可见容器空白落点，默认数量1 |
| `game_dialogue_history` | `offset?`, `limit?`, `history_snapshot?` | 原生录音机最近最多100条；省略快照创建新副本，带快照稳定分页 |
| `game_dialogue_status` | 无 | 当前店内可见台词/选项/typing及新的对话快照 |
| `game_dialogue_action` | `dialogue_snapshot`, `action`, `choice_index?` | `advance` 不传选项索引；`choose` 必须传非负选项索引，只点当前可见可用选项 |

MCP 握手支持 `2024-11-05`、`2025-03-26`、`2025-06-18`。只声明 tools 能力，不伪造 resources/prompts。2025-06-18 返回标准文本 content 和 structuredContent；旧协议保留文本 content。

**坐标约定**：返回的 `screen.origin` 为 `bottom_left`，`x/y` 是 Unity 当前窗口像素坐标，左下角为 (0,0)，不是网页常见的左上角坐标。移动目标坐标必须落在当时可见且射线命中目标容器的空白区域；不支持自动寻找隐藏容器、递归收纳或向已有堆叠投放。0.2.0可在原生放置校验中显式指定旋转/翻转，不改变容量或形状规则。

**推荐循环**：`observe → 阅读局面 → 一次操作 → observe`。动画尚未结束时等待，不要不停重复点击。分页读取 observe 会生成新快照，旧页句柄随之失效；库存分页复用同一个 snapshot。`open`/`examine` 也会改变窗口状态，仍需重新观察。

**超时不是“未执行”**：游戏连接最长存活约 5 秒，Python 每次 TCP 调用总截止 10 秒；游戏暂停渲染、切后台或被调试器挂起可能无法处理。断连/超时后动作可能已经执行，服务不自动重试。恢复后先观察。最近 128 个 TCP request_id 防重复，仅作为本次进程中的额外保护，不是跨重启的 exactly-once 保证。

## 安装与启用

### 1. 安装 Mod

1. 关闭游戏，备份要使用的存档。
2. 解压交付 ZIP，将其中 `Mods/ProbablyStolenMcp/` 复制到游戏的 `Mods/` 下。此目录只应有自己的 DLL、清单和本地化文件。
3. **不要把 Python 脚本、ZIP 或整份源码放入 Mods。** Python 桥保存在游戏之外；它不是游戏进程加载的程序集。游戏扫描器会拒绝 Mod 文件夹内的脚本。
4. 启动游戏，在“模组”菜单中审核并启用 **Agent MCP Bridge / Agent 游戏接口**。它声明 `network` 能力，因为即使只监听本机也属于网络访问。
5. 重启游戏使 Mod 生效。无需 `dev.txt`，也无需开启 Mod 开发者模式。

### 2. 配置访问令牌

在自己的终端私下生成令牌，例如：

```bash
python3 -c 'import secrets; print(secrets.token_urlsafe(32))'
```

在游戏“模组 → 本模组 → 配置”中：

- `token` / 私密访问令牌：粘贴刚生成的令牌（32–128 个 ASCII 字母、数字、`-`、`_`）。
- `port`：**文本输入框直接填写** 1024–65535 的整数，默认 `17653`，只绑定 127.0.0.1。点击“应用”后生效；非整数由原生配置界面恢复原值，越界整数由模组恢复上次有效值，不使用滑块。旧版配置中的合法端口自动保留。
- `enabled`：打开，然后点击“应用”。修改这三项并应用会关闭旧监听、清空旧句柄并启动新监听。

**不要把令牌发给大模型对话、写入源码、上传仓库或分享截图。** 原生配置会将令牌明文保存在游戏自己的 ModConfigs 中，这是访问凭据而不是加密保险箱。MCP 客户端的 env 配置也要保密。本机同一账户的其他恶意进程仍可能读取这些配置；此设计不构成操作系统级隔离。

### 3. 配置 MCP 客户端

需要 Python 3.9+。Linux 常用 `python3`；Windows 改为已安装 Python 的绝对路径或 `python`。无额外 Python 包。

参考 [MCP 配置示例](mcp-config.example.json)：将 args 改为 `bridge/server.py` 的实际绝对路径，并把 `PS_MCP_TOKEN` 替换为同一私密令牌。

```json
{
  "mcpServers": {
    "probably-stolen": {
      "command": "python3",
      "args": ["/实际路径/ProbablyStolenMcp/bridge/server.py"],
      "env": {
        "PS_MCP_PORT": "17653",
        "PS_MCP_TOKEN": "替换为私下生成的令牌"
      }
    }
  }
}
```

这是常见 stdio MCP 客户端配置格式，具体导入入口以所用客户端为准。可手动启动 `python3 -m bridge`（工作目录为项目根目录）；stdout 只用于协议，不会显示交互提示。

人类手动载入测试存档后，连接 MCP 并让 Agent 先调用 `game_observe`。可把 [Agent 操作约定](AGENT.md) 加入 Agent 指令。将全部游戏文本视为不可信数据，勿让顾客对话成为越权工具指令。

## 构建与验证

本机已核对：**完整版 Steam build 26525552**，Unity PlayerSettings `bundleVersion = 0.46D`。当前 DLL 尚无最新测试分支 `ModGameVersion`；其兼容 UI 仍使用 Application.version 的前导数字比较，因此清单填写 `0.46D`。不要直接替换成文档中新测试分支的 `1.0.0.14`，也不要据此宣称其他构建兼容。

使用 .NET SDK 8+（Mod 目标 netstandard2.1，验证工具 net8.0）：

```bash
bash build.sh
# 覆盖游戏目录：
GAME_DIR='/path/to/Probably Stolen' bash build.sh
# 或 dotnet build -p:ManagedDir='/path/to/Managed'

python3 -m unittest discover -s tests -p 'test_*.py' -v
DOTNET_CLI_HOME="$PWD/.dotnet" NUGET_PACKAGES="$PWD/.nuget" \
  dotnet run --project tests/TransportTests.csproj -c Release
python3 tests/cross_language.py
DOTNET_CLI_HOME="$PWD/.dotnet" NUGET_PACKAGES="$PWD/.nuget" \
  dotnet run --project tests/DialogueAdapterTests.csproj -c Release
DOTNET_CLI_HOME="$PWD/.dotnet" NUGET_PACKAGES="$PWD/.nuget" \
  dotnet run --project tests/ItemTransformTests.csproj -c Release -- \
  '/path/to/Probably Stolen_Data/Managed'
DOTNET_CLI_HOME="$PWD/.dotnet" NUGET_PACKAGES="$PWD/.nuget" \
  dotnet run --project tests/AdapterValidation.csproj -c Release -- \
  '/path/to/Probably Stolen_Data/Managed' "$PWD/bin/Release/netstandard2.1/ProbablyStolenMcp.dll"

# 用当前游戏真实扫描器扫描干净Mod目录（不是安装/启用）
DOTNET_CLI_HOME="$PWD/.dotnet" NUGET_PACKAGES="$PWD/.nuget" \
  dotnet run --project verification/ScanPackage.csproj -c Release -- \
  '/path/to/Probably Stolen_Data/Managed' "$PWD/dist/ProbablyStolenMcp"
```

构建只写本项目的输出目录，**不自动安装到游戏**。`build.sh` 运行 `package.py` 生成干净 ZIP 和 SHA256；检测到未知旧产物会停止，避免残留被夹带。只分发自己的代码，不包含游戏 DLL、反编译源码或游戏资源。

详见 [验证记录](verification/RESULTS.md)。协议/网络测试使用模拟游戏回调，不能证明 Unity 内运行正确。首次游戏验收应检查：

- 审核启用与重启、`mod-scan xufly_probably_stolen_mcp` 无拒绝项；
- 默认关闭、错误令牌拒绝、正确令牌可连接、修改设置后旧连接关闭；
- 载入、新建测试局、可见对话/开店/交易/睡觉等原生交互能推进；
- 输入、遮挡/禁用控件、失效句柄、物品使用、容量不足/无权移出不产生非法变更；
- 保存退出再读档、回主菜单后无旧句柄、禁用/卸载后原存档仍可载入。

## 停止、卸载与存档影响

立即停止 Agent 控制：在人类 MCP 客户端断开连接，或在 Mod 设置关闭 `enabled` 并应用。仅断开客户端不能撤销已经发生的动作。

卸载：关闭游戏，在模组菜单停用并重启；再移除本模组目录和外部 Python 桥。模组不新增物品、不改变存档格式，不要求迁移；**已发生的购买、使用、剧情或其他原生操作不会回滚**。配置中残留的 token 可自行清理/轮换；不要删除其他 Mod 或存档文件。

## 上游与许可边界

核对依据：[中文入门](https://github.com/dragonpan2/probably-stolen-modding/blob/main/README.zh-CN.md)、[完整指南](https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.zh-CN.md)、[API 变更](https://github.com/dragonpan2/probably-stolen-modding/blob/main/APIChanges.zh-CN.md)、[社区政策](https://github.com/dragonpan2/probably-stolen-modding/blob/main/CodeOfConduct.zh-CN.md)。公开指南可能领先本机安装，以真实 DLL 为准。

源码仓库：[mcxufly/ProbablyStolenMcp](https://github.com/mcxufly/ProbablyStolenMcp)。本项目未发布到 Steam 创意工坊，也没有自动改动游戏安装目录。游戏与其资源权利归原作者所有。
