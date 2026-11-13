# 0.2.0 真实 MCP 验收记录

日期：2026-11-13。仅通过 stdio MCP tools/call 控制游戏，没有桌面键鼠、截图、控制台、直接读写存档或游戏内任意代码调用。运行版本由 `game_observe.mod_version=0.2.0` 确认，场景 InventoryScene。

## 起始和当前状态

起始为第2天、545信用点，夜间报告打开。通过实际可见 CloseButton 关闭报告后读取店铺与库存。随后开店并依次呼叫当日顾客，未进行买卖、未睡觉、未跨日；当前第2天营业中，队列显示0，资金仍545。台词可能有原生教程/物品发放等后果，不能仅凭资金不变断言全部存档字段没有变化。

## 原生录音机历史：已通过

1. 起始空队列返回 total=0、entries=[]、source=native_recorder、native_capacity=100，属于合法结果。
2. 呼叫废料客后，历史返回4条：分隔标记及三句话。限limit=2分页，前后页分别记录index0/1和2/3，next_offset正确。
3. 在两页之间调用game_observe，history_snapshot仍有效，页内容不漂移。
4. 通过原生Recorder世界句柄打开录音机，UI可见三句与history raw_text一致；界面枚举次序与队列顺序不同，专用history按原生队列顺序返回。验证后关闭录音机。
5. 新顾客到来后，新history逐步增长到6/10/18条，保留角色名和分隔，不再依赖正好采集当前气泡。
6. 随机错误history_snapshot被拒；创建新history快照后，旧历史快照也被拒。
7. 普通observe及失败的dialogue_action不会破坏仍在TTL内的当前历史分页副本。

本轮原文样本：废料客“喂，我带了些材料来卖。”“全要的话，我给你打六七折。”“只要找到合适的买主，这些东西很容易就能脱手。”

没有测试超过100条截断边界的运行场景、跨天/进程/读档历史保存或断线无损恢复，不能从当前成功扩大推断。

## 店内对话状态与继续：已通过

1. 无对话时status返回active=false、typing=false、can_advance=false；带最新快照请求advance返回unavailable，不触发剧情。
2. 观察下层区修理工与拾荒客台词：能捕获active=true/typing=true/can_advance=false，到typing=false/can_advance=true的原生变化。
3. typing时请求advance明确被拒：“dialogue is still typing; wait instead of skipping”。没有调用skip。
4. 拾荒客说“我昨天找到了一箱泥炭！”打字完成后，使用当时dialogue_snapshot执行advance，返回performed=true；下一次status为下一句“你全要的话，我给你打八五折。”且typing=true，构成实际原生推进证据。
5. 重用已执行dialogue_snapshot返回unavailable/expired；读取status后再game_observe，旧对话动作快照也被拒，符合三快照域设计。
6. 导师多句教程可通过status连续读取，且原生录音机历史继续累积。raw_text是整句开始时已有全文，visible_character_limit在本次TMP实现常为99999，真正动画状态由typewriter.isShowingText报告，不能把maxVisibleCharacters当严格逐字进度。

本轮没有遇到选择分支，choices为空；**choose仍未游戏内验收**，未构造隐藏选项或盲选剧情。电话对话不在本接口范围。

## 物品旋转/翻转：已通过可恢复移动

选择原背包一支牙膏，原状态cell_x=12、cell_y=0、rotation=0、flipped=false，占用3×1。

- 对可见背包的empty_drop_point执行game_move_item(rotation=90,flipped=true,amount=1)，返回moved=1。
- 下一次observe实际状态：cell_x=12、cell_y=4、rotation=90、flipped=true、占用1×3，真实渲染bounds也为竖形。
- 用新观察句柄和原位置，将rotation=0、flipped=false移回，返回moved=1。
- 最终牙膏恢复cell(12,0)、3×1、0度、未镜像；另一支仍在cell(9,0)。数量和资金不变。

未测试特殊槽强制方向拒绝、堆叠拆分旋转、跨存档还原，也未测试所有形状。

## 配置与其他范围

当前没有candidate_actions含configure的可见物品，因此configure尚未真实验收；没有生成机器或绕过物品获取来凑测试。原有物品打开、窗口关闭、原生库存、世界描述仍正常。

## 结论

0.2.0新接口中，原生history及稳定分页、当前状态/typing、原生advance/快照防重、可恢复旋转镜像已实际通过；没有在本轮发现需要修改DLL的缺陷。choose、configure与其余未支持玩法继续明确保留待测/待实现。此记录是v0.2.0实现标签后的文档补充，不覆盖tag、不宣称当前所有玩法已完成。
