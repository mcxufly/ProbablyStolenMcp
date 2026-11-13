# Probably Stolen MCP / 深空当铺 Agent 接口

**0.1.0，本地开发预览版。** 为《深空当铺：可能是偷的》（Steam App 4348910）提供可由大模型 Agent 调用的游戏观察与操作工具。使用游戏原生 Mod 加载器；Agent 调用标准 **MCP stdio**，由 Python 桥转发到游戏内的本机 TCP 服务。

> 自动测试不等于游戏实测。目前已核对本机真实游戏程序集并构建，但尚未安装到游戏、未用真实存档完成游玩验收。请先备份，使用测试存档；本版本不承诺无人值守通关。操作可能消耗物品、花费信用点、推进剧情并永久改变存档。

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
- 这是语义/控件接口，不是屏幕视频、鼠标键盘注入或通用视觉 Agent。复杂的按住交互、旋转/翻转物品及特殊小游戏等可能仍需人类操作；详情见 [API 核验说明](verification/API-NOTES.md)。

## MCP 工具

| 工具 | 参数 | 用途 |
|---|---|---|
| `game_observe` | `offset?`, `limit?` | 读取当前可见局面、文本、原生 UI/场景控件，生成新 snapshot 与句柄；分页默认值由游戏适配层给出，limit 1–100 |
| `game_ui_action` | `snapshot`, `handle`, `action?` | `click`（默认）、`double_click`、`scroll_up`、`scroll_down`；只允许该句柄支持的动作 |
| `game_ui_input` | `snapshot`, `handle`, `value` | 填写文本；开关传字符串 `true`/`false`，下拉框传索引字符串，滑块传有限数字字符串；最长 2048 字符 |
| `game_inventory` | `snapshot`, `offset?`, `limit?` | 展开该快照已发现的可见物品/容器；不刷新 snapshot |
| `game_item_action` | `snapshot`, `handle`, `action` | 原生 `open`、`examine`、`activate`、`toggle`；不开放无法完整收尾的选择待用/指定作用目标流程 |
| `game_move_item` | `snapshot`, `handle`, `target`, `x`, `y`, `amount?` | 将物品移动到当前可见的目标容器位置，默认数量 1；实时检查位置、源可移出、目标接受规则和容量 |

MCP 握手支持 `2024-11-05`、`2025-03-26`、`2025-06-18`。只声明 tools 能力，不伪造 resources/prompts。2025-06-18 返回标准文本 content 和 structuredContent；旧协议保留文本 content。

**坐标约定**：返回的 `screen.origin` 为 `bottom_left`，`x/y` 是 Unity 当前窗口像素坐标，左下角为 (0,0)，不是网页常见的左上角坐标。移动目标坐标必须落在当时可见且射线命中目标容器的空白区域；不支持自动寻找隐藏容器、递归收纳、向已有堆叠投放或旋转/翻转形状。

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
- `port`：默认 `17653`，只绑定 127.0.0.1。
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

本项目没有上传 GitHub 或 Steam 创意工坊，也没有自动改动游戏安装目录。游戏与其资源权利归原作者所有。
