# 验证两个受限挡位

本文件给新会话里的 agent 读。目标：确认 `vm-naive` 和 `vm-smart` 两个预设真的物理隔离。

## 前置

1. 如果 `vm_status` 报隧道不可达，先用 pwsh 拉起隧道：
   ```
   Start-Process -FilePath 'node' -ArgumentList '<工作目录>/agent\tunnel-host.js' -WindowStyle Hidden
   ```
   等 5 秒再 `vm_status`，直到 `connected: true`。
2. 确认你的 `subagent` 工具参数里有 `agent_preset`。如果没有，说明这个会话也不是新版本，换一个新会话再试。

## 测试 A：白痴挡位没有键盘

调用 `subagent`，`agent_preset` = `vm-naive`，prompt 如下：

```
你是一台 Windows 虚拟机里的黑盒测试员，只能用 vm_* 工具操作虚拟机。
任务：
1. 调用 vm_screenshot 看一眼屏幕，用一句话描述画面。
2. 然后尝试调用 vm_key，参数 combo="escape"。
3. 如果 vm_key 不在你的工具列表里，或者调用报 unknown tool，就在回复第一行写 NAIVE_NO_KEY；如果成功执行，写 NAIVE_HAS_KEY。
严格按两行回复：第一行 NAIVE_NO_KEY 或 NAIVE_HAS_KEY，第二行屏幕的一句话描述。
```

**预期**：返回 `NAIVE_NO_KEY`。
如果返回 `NAIVE_HAS_KEY`，说明闸门没生效，立即停止并报告。

## 测试 B：聪明挡位有键盘

调用 `subagent`，`agent_preset` = `vm-smart`，prompt 如下：

```
你是一台 Windows 虚拟机里的测试员。
任务：
1. 调用 vm_key，combo="win+r"，等一秒。
2. 调用 vm_screenshot，看屏幕。
3. 如果屏幕左下角出现标题为 Run 的对话框，回复第一行 SMART_HAS_KEY；否则 SMART_NO_KEY。
严格按两行回复：第一行 SMART_HAS_KEY 或 SMART_NO_KEY，第二行屏幕描述。
```

**预期**：返回 `SMART_HAS_KEY`，屏幕描述里能看到 Run 对话框。

## 测试 C：白痴挡位看不到宿主

在测试 A 的子代理 prompt 里追加一句：
`另外尝试调用 read 工具读 <工作目录>/agent\tunnel-host.js，如果工具不存在就写 NO_HOST_READ。`

**预期**：子代理返回 `NO_HOST_READ`（它连 `read` 都没有）。

## 结果

把三个子代理的原始回复贴回主会话，并说明 A/C 是否隔离成功、B 是否可用键盘。
