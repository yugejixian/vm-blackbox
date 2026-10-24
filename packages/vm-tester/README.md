# vm-tester

> 给 DeepSeek Harness 用的黑盒 GUI 测试插件：让 AI 测试员在真实虚拟机里**只看屏幕、只点鼠标**地测软件。
>
> Black-box GUI testing for the Harness: the agent sees nothing but screenshots and drives nothing but mouse and keyboard. Judgement must be backed by pixels.

## 为什么做这个

AI 跑自动化测试时最容易出的问题不是点不对，而是**它可以看到日志、源码、内部状态，于是"知道"自己没错**——测试就变成了自我确认。

这个插件把测试员放进一台真实的 Windows 虚拟机，只给它七个工具：截图、点击、输入文字、按键、等待。它看不到日志、进程、注册表，也读不到宿主的任何东西。结论只能靠截图，截图可以被独立复核——这就是"真实"的来源。

## 架构

```
┌──────────────┐   工具调用   ┌───────────────┐   HTTP    ┌──────────────┐
│ DSH 测试员   │ ───────────▶ │ @local/vm-    │ ────────▶ │ tunnel-host  │
│ (vm-naive /  │              │  tester 插件  │  127.0.0.1 │ .js (宿主)   │
│  vm-smart)   │ ◀─────────── │               │    :8890  │              │
└──────────────┘   图片+结果   └───────────────┘           └──────┬───────┘
                                                                  │ 反向社会话
                                                          ┌───────▼───────┐
                                                          │ QaTunnel.exe  │
                                                          │ (虚拟机内代理) │
                                                          └───────┬───────┘
                                                                  │ SendInput / GDI
                                                          ┌───────▼───────┐
                                                          │ Windows 虚拟机 │
                                                          └───────────────┘
```

关键点：**虚拟机主动连宿主**（反向隧道），所以不需要给虚拟机开入站端口，宿主的防火墙保持关闭也能用。

## 本包提供的七个工具

| 工具 | 作用 |
|---|---|
| `vm_status` | 隧道是否连通、虚拟机分辨率、当前登录用户 |
| `vm_screenshot` | 截当前屏幕，返回图片；图上带**坐标标尺**（每 200px 一条线，标原生像素）和**上次点击的准星** |
| `vm_click(x,y)` | 左键点击（原生像素坐标） |
| `vm_type(text)` | 往当前焦点输入文本（支持中文，走 Unicode 注入） |
| `vm_key(combo)` | 按一个键或组合键：`enter` / `esc` / `win+r` / `ctrl+a` … |
| `vm_key_basic(key)` | 只允许普通键（给"傻子用户"挡位用，防止它作弊用快捷键） |
| `vm_wait(ms)` | 等待 |

`vm_smart` 挡位拿到全部五个核心工具；`vm_naive` 只拿到 `vm_key_basic`，模拟一个只会回车和 Tab 的普通用户。

## 依赖（光装这个包不够）

这七个工具只是"遥控器"，真正的虚拟机侧还需要三样东西，它们**不在这个 npm 包里**（体积与平台相关，需要单独部署）：

1. **VirtualBox 7.x** + 一台 Windows 虚拟机（推荐中文镜像）
2. **`QaTunnel.exe`**：跑在虚拟机内的代理，开机自启，反向连宿主 `8801`；负责截屏（含坐标标尺）与 `SendInput`
3. **`tunnel-host.js`**：跑在宿主的 Node 服务，监听 `8801`（等虚拟机连入）并把能力摊成 `8890` 的 HTTP 接口

完整部署脚本与踩坑记录见项目仓库的 `build-vm.ps1` 与 `handoff.md`。

## 安装

```bash
# 标准方式（Harness 的插件管理器）
dsh plugin add /path/to/vm-tester
```

或者在 profile 的 `package.json` 里登记为 bundle：

```jsonc
{
  "dependencies": { "@local/vm-tester": "file:/path/to/vm-tester" },
  "dsh": { "profile": { "bundles": ["@local/vm-tester"] } }
}
```

> 注意事项：如果你的 profile 里已经手工插入过同样 `id: vm-tester` 的 patch 行，**必须先删掉**，否则会重复加载同一插件导致启动失败。
>
> 作者本机曾因插件管理器的 pnpm 安装在 `@img/sharp-win32-x64` 上报 `EPERM`，把上千个包挪进 `node_modules/.ignored` 而损坏 profile。发布前请在**隔离 profile** 里完整跑一遍安装与卸载。

## 配置

```yaml
- id: vm-tester
  name: '@local/vm-tester'
  config:
    tunnelUrl: 'http://127.0.0.1:8890'   # tunnel-host.js 的 HTTP 地址
    shotsDir: 'D:\null_work\shots'        # 截图落盘目录（证据）
```

## 安全边界

- 测试员只能操作**虚拟机**；插件不提供任何宿主文件、命令、网络能力。
- 建议配合 `@local/tool-gate` 用 allow 白名单把每个测试挡位的工具锁死（`vm-naive` 拿 4–5 个，`vm-smart` 拿 5 个）。
- 虚拟机里请用独立账号、UAC 提权提示建议关闭（否则合成输入进不了安全桌面），并关掉防火墙拦截通知。

## 已知限制（都是实测的）

| 限制 | 说明 |
|---|---|
| 帧率约 1 帧/秒 | 每次截图起一次抓屏，够看清操作，不是视频 |
| 只有左键 | 没有右键菜单；需要用应用本身的命令栏按钮 |
| 合成双击对 `.exe`/`.lnk` 无效 | 打开项目请用「单击选中 + Enter」 |
| 界面重排会毁掉旧坐标 | 例如 Windows 计算器显示表达式行时按钮下移；**每次点击前重新截图** |
| 部分 3D 应用抓不到画面 | GDI 抓屏遇 D3D 交换链会超时；本包对 1 帧/秒的桌面类应用稳定 |
| 提权窗口 | 代理需以「计划任务 + 最高权限 + 交互式」运行，否则 UIPI 会丢弃输入 |

## 许可

MIT，见 [LICENSE](./LICENSE)。
