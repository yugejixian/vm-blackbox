# dsh-vm-tester — 在虚拟机里像人一样测软件

> 给 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 用的黑盒 GUI 测试插件：AI 测试员**只看屏幕、只点鼠标**，判定必须由像素支撑。
>
> Black-box GUI testing for DSH: the agent sees nothing but screenshots and drives nothing but mouse and keyboard.

## 为什么

AI 跑自动化测试最容易失效的地方不是"点不准"，而是**它能读到日志、源码、内部状态，于是"知道"自己没错**——测试就退化成了自我确认。

这个插件把测试员扔进一台真实的 Windows 虚拟机，只给它截图和键鼠：看不到日志、进程、注册表，也够不到宿主。结论只能靠像素，而像素可以被独立复核。这就是"真实"的来源。

## 架构

```
DSH 测试员 ──工具调用──▶ dsh-vm-tester ──HTTP :8890──▶ tunnel-host.js (宿主)
                                                              ▲
                                                              │ 反向隧道 :8801
                                                     QaTunnel.exe (虚拟机内)
                                                              │ SendInput / GDI
                                                              ▼
                                                     Windows 虚拟机（真实桌面）
```

虚拟机主动外连宿主，**不需要给虚拟机开入站端口**。

### 用 vm_ui 代替肉眼估坐标（推荐）

```jsonc
// 1) 确认前台窗口
vm_status()                       // -> foregroundWindow: "Calculator"

// 2) 取元素树，按名字/ID 找控件
vm_ui({ depth: 6, maxNodes: 200 })
// elements: [{ type:"Button", name:"Seven", cx:90, cy:376, visible:true }, ...]

// 3) 直接用树里的 cx/cy 点击
vm_click({ x: 90, y: 376 })       // 点中 "7"
```

要点：
- 过滤 `visible:true` 且 `enabled:true`；隐藏元素会返回 `w/h = 0`。
- 只覆盖**前台窗口**；要操作别的窗口先点击/`Alt+Tab` 让它到前台。
- 拿到坐标后窗口若移动，需要重新取树。

## 通用测试工作流（任意软件，不限象棋）

```jsonc
// 1) 查看环境基准
vm_snapshot({ action: "list" })        // { snapshots:["base-clean","base-disguised"], current:"base-disguised" }

// 2) 重置到想要的基准（干净 VM / 已伪装 VM）
vm_snapshot({ action: "restore", name: "base-clean" })

// 3) 投递被测软件（压缩包或目录都行）
vm_upload({ localPath: "D:\\assets\\app.zip", guestPath: "C:\\Users\\qa\\Desktop\\app.zip" })
vm_upload({ localPath: "D:\\assets\\suite", guestPath: "C:\\Users\\qa\\Desktop\\suite", recursive: true })

// 4) 用截图/键鼠（或 vm_ui）操作、判定
vm_screenshot(); vm_click({x,y}); vm_key({combo:"enter"})

// 5) 取回证据
vm_download({ guestPath: "C:\\Users\\qa\\AppData\\Local\\Temp\\app.log", localPath: "D:\\evidence\\app.log" })

// 6) 下一个测试前再重置
vm_snapshot({ action: "restore", name: "base-disguised" })
```

要点：
- 两个基准快照**保持为空**（不装任何特定软件），软件靠 `vm_upload` 临时投递，测完回滚即消失。
- 需要测试「反 VM 检测」类软件时用 `base-disguised`；普通功能测试用 `base-clean`。
- `restore` 大约需要 2–3 分钟（关机 + 恢复 + 开机 + 代理重连）。

## 新会话开场：一条命令就绪

新会话不需要重复部署。先调用 `vm_status`；如果 `connected=false`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\session-ready.ps1 -VmName "<虚拟机名>" -GuestPass "<密码>"
```

它会：确认/修复 NAT、自动开机、校验并启动访客代理、连接失败自动重启代理与隧道、做一次真实回环校验，最后打印就绪耗时。

参考耗时（实测）：

| 环境状态 | 耗时 |
|---|---|
| 热（VM+隧道都在跑） | **0.2 秒** |
| 冷（VM 关机，需要开机） | **40–60 秒** |
| 连接损坏（自动修复） | **约 9 秒** |

> 首次环境建设（装系统、防病毒排除、反 VM 伪装、许可协议等）是一次性的，已经固化在快照与脚本里，不需要每个会话重做。

## 两种观测模式：严格像素 vs 辅助定位

这套工具的动作通道**永远只有键盘和鼠标**（`vm_click` / `vm_type` / `vm_key`），物理隔离也在虚拟机边界内。
差别只在「观测通道」：

| 模式 | 配置 | 观测通道 | 适用 |
|---|---|---|---|
| **辅助定位**（默认） | `strictPixelOnly: false` | 截图 + `vm_ui` 元素树 + 前台窗口标题 | 日常测试，快 |
| **严格像素** | `strictPixelOnly: true` | **只有截图**（`vm_ui` 拒绝、`vm_status` 不返回窗口标题） | 严格黑盒审计、要证明「只靠像素」时 |

```yaml
# cordis.patch.yml
config:
  tunnelUrl: "http://127.0.0.1:8890"
  strictPixelOnly: true   # 严格模式：只剩截图 + 键鼠
```

**无论哪种模式，判定都必须由截图支撑**：`vm_ui` 只用来「找按钮在哪」，不能拿来当「功能是否正常」的证据。

## 被测软件要求管理员权限 / 反虚拟机检测（实测经验）

### 1) UAC 会把「傻子测试员」挡死在门外

程序要求管理员权限时，Windows 在**安全桌面**弹 UAC。安全桌面上：
- 访客代理截不到图（/shot 报错），`vm_status.pointerOk = false`
- 点击/输入全部无效（代理是非提权进程）

表现很容易被误判成「虚拟机检测」或「桌面卡死」。两种处理：

```powershell
# A. 宿主侧注入键盘批准（推荐，穿透安全桌面）
powershell -ExecutionPolicy Bypass -File scripts\uac-approve.ps1 -VmName "<虚拟机名>"

# B. 长期测试：访客内以管理员运行，关闭 UAC（重启生效）
#    先把 scripts\uac-off.ps1 拷进访客，再以管理员身份运行
```

### 2) 提权后的程序，非提权代理点不到

Windows 的 UIPI 会拦掉低完整性进程对高完整性窗口的输入。关闭 UAC（上面方案 B）后，
代理与程序同为完整权限，就能正常点击。

### 3) 硬件伪装打不过 CPUID 检测

实测某象棋程序（要求管理员权限）在伪装后的虚拟机上仍然弹出：

> Sorry, this application cannot run under a Virtual Machine.

`scripts/disguise-vm.ps1` 改的是 SMBIOS/磁盘/MAC 这些**资产标识**，
而这类检测通常读 **CPUID 的 hypervisor 位**（VirtualBox 无法直接隐藏）。
也就是说：伪装能骗过「看型号」的检测，骗不过「看虚拟化指令」的检测。

### 4) 截图不会因为安全桌面而完全失明

`/shot` 失败时会自动回退到**宿主 framebuffer** 截图（响应头 `X-Shot-Via: host-fallback`），
所以 UAC 窗口、安全桌面也能被看见——但此时仍**无法点击**，需要人工/脚本批准。

## 虚拟机硬件伪装（可选，测试「反 VM 检测」类软件用）

有些被测软件会检测自己是不是跑在虚拟机里。可以用脚本把**自有虚拟机**伪装成一台常见物理机，观察软件行为差异。

```powershell
# 应用伪装（SMBIOS 厂商/型号、BIOS、硬盘/光驱型号、MAC）
powershell -ExecutionPolicy Bypass -File scripts\disguise-vm.ps1 -VmName "<虚拟机名>"

# 还原
powershell -ExecutionPolicy Bypass -File scripts\disguise-vm.ps1 -VmName "<虚拟机名>" -Revert
```

设置需要**关机后开机**生效；开机后可在虚拟机里用 `systeminfo`、`wmic csproduct get vendor,model` 检查。

> 说明：这只是把硬件标识换成常见值，方便测试兼容性/检测行为；请只用于你自己的环境，不要用它规避你不被允许运行的软件的限制。
> 伪装后**不要卸载 Guest Additions**，否则 dsh-vm-tester 的隧道会断。

## 故障排查：画面冻结 / 输入失效

症状：**截图还在更新（或停在某一刻）、鼠标键盘完全无反应**，但虚拟机里 PowerShell 还能执行。
这通常是**访客桌面会话卡死**（常见于：虚拟机保存状态后恢复、全屏程序崩溃、来回切换 VirtualBox 窗口/显示模式）。

自查：

```jsonc
vm_status()  // -> pointerOk: false，或者
vm_click({x:100,y:100})  // -> error: "pointer does not move: guest desktop session looks hung ..."
```

处理：**重启虚拟机**（`VBoxManage controlvm <vm> reset` 或 guest 里 `shutdown /r /f /t 5`）。
重启后 `pointerOk` 恢复 `true`，点击即可用。

> 建议：测试期间**别用「保存状态」**暂停虚拟机，改用关机；也尽量别在宿主上打开 VirtualBox 图形窗口反复切显示模式。

## 输入与关闭的硬规则（实测踩坑总结）

> 这些是真实首次使用时踩到的坑，按这三条做能省很多时间。

1. **`vm_type` 之后核对字符数**：用 `vm_status` 看 `foregroundWindow` 确认窗口没错，再看可见文本/状态栏字符数。
   注意计数按 **Unicode 码点**：`😀` 计 1，但记事本状态栏可能显示 2（UTF-16 代理对）——不是丢字。
2. **关窗统一用 `vm_key("alt+f4")`**：对应用窗口有效；只有**桌面/任务栏**上的 Alt+F4 会被拒绝（防止弹出「关闭 Windows」误关机）。
   不要点标题栏右上角 ×：它和最大化按钮挨得很近，窗口位置稍偏就会命中贴靠布局。
3. **`Win+R` 不是空的**：它会预填上一条命令并全选，直接回车会跑错程序。先 `Ctrl+A` 再输入，或先看 `vm_status.foregroundWindow` 确认。

### 关于截图来源

`vm_screenshot` 返回里可能带 `capture=guest` 或 `capture=host`：

| 来源 | 含义 |
|---|---|
| `guest` | 访客自己抓的帧（正常路径，带标尺/光标/遮罩） |
| `host` | 宿主 framebuffer 抓帧（访客帧连续不变时的兜底，例如全屏视频/3D 窗口） |

两者坐标都是**访客原生像素**，可直接用于 `vm_click`。

### Win11 记事本会自动恢复会话

关闭记事本不会弹保存框，下次打开会恢复上次的标签内容——这不等于「保存了」也不等于「清空了」。
要得到空白页：打开后新建标签（Ctrl+N），或先在记事本里关掉所有标签。

## 危险按键防护

测试员可能会误按 **桌面上 Alt+F4** —— 那会弹出「关闭 Windows」，等于把虚拟机直接关掉。
代理会拒绝这类操作并返回明确错误：

```
vm_key("alt+f4")  ->  refused: Alt+F4 on the desktop/taskbar would open the shutdown dialog
```

正常窗口（记事本、浏览器等）上的 Alt+F4 仍然允许关闭。

## 自动清理（默认开启）

截图不会无限增长，三层规则：

| 层 | 规则 |
|---|---|
| 插件 | 截图超过 **72 小时**或超过 **300 张**自动删除（可配置 `shotsKeepHours` / `shotsKeepFiles`） |
| 隧道 | 抓帧临时文件（`gc-*`/`gco-*`/`raw-*`）超过 10 分钟自动删，运行中每 5 分钟扫一次 |
| 系统 | `quick-setup.ps1` 安装「登录时自动清理」（有管理员权限时改为每日 03:30 任务） |

手动清理：`powershell -ExecutionPolicy Bypass -File scripts/clean-shots.ps1`
安全约束：只删严格命名的自有文件、只处理目录顶层、拒绝系统/用户根目录、非默认目录需显式 `-AllowCustomDir`。

## 十二个工具

| 工具 | 作用 |
|---|---|
| `vm_status` | 隧道是否连通、分辨率、当前登录用户 |
| `vm_screenshot` | 截当前屏幕；图上带**坐标标尺**（每 200px 标注原生像素）和上次点击的准星 |
| `vm_ui` | **UI 元素树**（前台窗口）：类型/名称/ID/矩形/中心点，按名字精确点，不用肉眼估坐标 |
| `vm_snapshot` | **环境基准管理**：list / take / restore。restore 会关机→恢复快照→开机，用于每次测试前重置环境 |
| `vm_upload` | 把宿主上的**文件/文件夹送进虚拟机**（投递被测软件、测试数据、脚本） |
| `vm_download` | 把访客里的**证据取回宿主**（日志、崩溃转储、输出文件） |
| `vm_click(x,y)` | 左键点击（原生像素坐标） |
| `vm_type(text)` | 往当前焦点输入文本（支持中文，Unicode 注入） |
| `vm_type_secret(name)` | 从宿主凭据库注入密码/令牌，**明文不进模型上下文**；注入后相关区域自动遮蔽 |
| `vm_key(combo)` | 按一个键或组合键：`enter` / `esc` / `win+r` / `ctrl+a` … |
| `vm_key_basic(key)` | 只允许普通键（给"只会回车的普通用户"挡位用，防止它用快捷键作弊） |
| `vm_wait(ms)` | 等待 |

## 快速开始

**第一次用？一条命令部署：`scripts/quick-setup.ps1`（见 [docs/SHARE.md](docs/SHARE.md)）；完整手动流程看 [docs/QUICKSTART.md](docs/QUICKSTART.md)** —— 保姆级流程，从装插件到第一次截图点击，含常见问题排查表。

## 安装

```sh
# 方式一：从 npm 安装（推荐）
dsh plugin add dsh-vm-tester

# 方式二：从 Releases 页面下载 .tgz 后安装（不写死版本）
#   下载页：https://github.com/yugejixian/vm-blackbox/releases
dsh plugin add C:\path\to\dsh-vm-tester-x.y.z.tgz

# 方式三：从本仓库安装
dsh plugin add github:yugejixian/vm-blackbox
```

> **装完只是遥控器，还不能用**：虚拟机、访客代理、宿主隧道三者缺一不可。
> 完整步骤见 [docs/QUICKSTART.md](docs/QUICKSTART.md)。若你的安装方式拿不到 `agent/` 目录（较早的 npm 版本），
> 请改用 `dsh plugin add github:yugejixian/vm-blackbox`，它包含全部脚本。

> 如果你的 profile 里已经手工插入过同样 `id: vm-tester` 的 patch 行，**必须先删掉**，否则会重复加载同一插件导致启动失败。

## 依赖：光装插件不够

插件只是"遥控器"，虚拟机侧还要三样东西（体积与平台相关，**不在 npm 包里**）：

1. **VirtualBox 7.x** + 一台 Windows 虚拟机（推荐中文镜像）；
2. **`QaTunnel.exe`**（[`agent/`](agent)）：跑在虚拟机内、开机自启，反向连宿主 `8801`，负责截屏（含坐标标尺）与 `SendInput`；
3. **`tunnel-host.js`**（[`agent/`](agent)）：跑在宿主的 Node 服务，监听 `8801` 并把能力摊成 `8890` 的 HTTP 接口。

一键建虚拟机脚本 [`scripts/build-vm.ps1`](scripts/build-vm.ps1)，完整部署与踩坑记录见 [`docs/handoff.md`](docs/handoff.md)。

## 配置

```yaml
- id: vm-tester
  name: 'dsh-vm-tester'
  config:
    tunnelUrl: 'http://127.0.0.1:8890'   # tunnel-host.js 的 HTTP 地址
    shotsDir: 'C:\Users\你\Pictures\vmshots'  # 截图落盘目录（留空则用默认值）
```

## 仓库结构

| 目录 | 内容 |
|---|---|
| 根目录 | npm 包本身：`index.js`、`package.json`、`cordis.patch.yml`、`locale/`、`icon.svg` |
| [`agent/`](agent) | 虚拟机内代理、宿主隧道、标定工具 |
| [`scripts/`](scripts) | 建虚拟机、窗口/无窗口切换、运行体检、截图清理、桌面围观小窗 |
| [`cases/`](cases) | 九条示例用例（记事本/计算器/安装/取消/卸载/游戏/资源管理器/浏览器/安全登录） |
| [`CHANGELOG.md`](CHANGELOG.md) | 版本变更记录 |
| [`docs/`](docs) | 部署交接、打包清单、发布检查表 |
| [`examples/`](examples) | 测试员提示词模板、执行报告模板 |

## 安全边界

- 测试员只能操作**虚拟机**；插件不提供任何宿主文件、命令、网络能力。
- 建议配合工具白名单把每个挡位的工具锁死（`vm-naive` 拿 4–5 个，`vm-smart` 拿 5 个）。
- 虚拟机里请用独立账号，建议关闭 UAC 提权提示（否则合成输入进不了安全桌面），并关掉防火墙拦截通知。

### 凭据与敏感信息（重要）

观察是**基于截图**的，测登录类场景必须按下面的方式做：

1. **虚拟机里绝不放假凭据**：只用一次性测试账号，测完禁用；
2. **密码走凭据库**：宿主侧 `scripts/vault-set.ps1 -Name <名字>`（DPAPI 加密），测试员用
   `vm_type_secret("<名字>")` 注入 —— **明文永远不进模型上下文**；
3. **自动遮蔽**：注入后代理把焦点控件与窗口标题栏涂黑 180 秒（宿主抓帧路径同样遮），
   也可以用 `vm_screenshot(mask=[{x,y,w,h}])` 手动指定区域（OTP 框、token 栏等）；
4. **不要对密码框 / OTP / 带 token 的地址栏截图**，需要验证就截提交后的页面。

残留风险（长度泄漏、应用自身回显、窗口移动后遮罩失准）见 `docs/handoff.md` 的「敏感数据与登录场景」。

## 已知限制（都是实测的）

| 限制 | 说明 |
|---|---|
| 帧率约 1 帧/秒 | 每次截图起一次抓屏，够看清操作，不是视频 |
| 只有左键 | 没有右键菜单；需要用应用本身的命令栏按钮 |
| 合成双击对 `.exe`/`.lnk` 无效 | 打开项目请用「单击选中 + Enter」 |
| 界面重排会毁掉旧坐标 | 例如 Windows 计算器显示表达式行时按钮下移；**每次点击前重新截图** |
| 提权窗口 | 代理需以「计划任务 + 最高权限 + 交互式」运行，否则 UIPI 会丢弃输入 |
| 部分 3D 应用抓不到画面 | GDI 抓屏遇 D3D 交换链会超时；桌面类应用稳定 |

## 许可与免责

MIT，见 [`LICENSE`](LICENSE)。

**使用前请阅读 [`DISCLAIMER.md`](DISCLAIMER.md)**：软件许可、终端用户协议（EULA）、隐私与合规责任由使用者自行承担。本仓库不含任何操作系统镜像、商业软件或游戏资源。
