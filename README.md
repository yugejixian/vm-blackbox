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

## 八个工具

| 工具 | 作用 |
|---|---|
| `vm_status` | 隧道是否连通、分辨率、当前登录用户 |
| `vm_screenshot` | 截当前屏幕；图上带**坐标标尺**（每 200px 标注原生像素）和上次点击的准星 |
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
