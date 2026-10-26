# Changelog

## 0.1.5 — 2026-10-26

### Fixed
- 部署脚本在**含空格或中文的安装路径**下无法启动隧道（PowerShell 5.1 不会自动给参数加引号）。
- 隧道程序的 PowerShell 路径改为读取 `%SystemRoot%`，不再假定系统装在 C 盘。

### Docs
- 安装说明改为路径占位符，并明确「装在哪个盘都可以、含空格或中文要加引号」。


## 0.1.4 — 2026-10-26

### Added
- `scripts/quick-setup.ps1`：一键部署（检查环境 → 投递访客代理 → 设置开机自启 → 启动宿主隧道 → 等待连通）。
- `docs/SHARE.md`：分享版安装与配置说明（三条不经过市场的安装途径）。
- `agent/mk-agent-startup.ps1`：在访客里创建代理的开机自启快捷方式。

### Fixed
- **隧道在插件包内无法启动**：根 `package.json` 声明了 `type: module`，导致 `agent/tunnel-host.js`（CommonJS）报 `require is not defined`。新增 `agent/package.json` 将 agent 目录标记为 CommonJS。
- 部署脚本在代理已运行时重复启动第二个实例；现在会比对版本后跳过。
- 部署脚本对「虚拟机刚恢复/刚启动、访客未就绪」的情况增加等待与重试。


## 0.1.2 — 2026-10-26

### Added
- `docs/QUICKSTART.md`：保姆级部署流程（装插件 → 准备虚拟机 → 部署访客代理 → 启动宿主隧道 → 冒烟测试 → 排错表）。
- npm 包自包含：包含 `agent/`、`scripts/`、`docs/`、`cases/`、`assets/`，从 npm 安装即可完成全部部署。

### Improved
- 截图目录默认使用 `%LOCALAPPDATA%\dsh-vm-tester\shots`（可用配置项 `shotsDir` 覆盖）。
- `cordis.patch.yml` 的 `shotsDir` 改为可选。
- 隧道与构建脚本的路径改为环境变量 / 相对目录，换机器无需修改脚本。

## 0.1.1 — 2026-10-25

### Added

- **`vm_type_secret(name)`**：从宿主凭据库（DPAPI 加密）读取密码并注入，明文不进入模型上下文；
  注入后代理会把焦点控件与窗口标题栏自动涂黑 180 秒（含宿主抓帧路径）。
- **`vm_screenshot(mask=[{x,y,w,h}])`**：截图离开虚拟机之前，按坐标涂黑敏感区域（OTP 框、token 栏等）。
- 截图工具返回 `capture=guest|host`，标明这帧来自访客抓屏还是宿主帧缓冲。

### Fixed

- 中文凭据在「凭据库 → 隧道」之间被转码（实测长度 12→14、SHA-256 不符）。
- Chromium / Electron 应用被整页遮蔽（焦点窗口 = 浏览器主窗口）；改用 UI Automation 取真实焦点元素。
- `vm_screenshot` 缺少 `via` 变量导致工具报错。
- 3D / GPU 合成窗口截到的是旧帧：新增陈旧帧检测，自动切换宿主帧缓冲。
- 代理在快照恢复后不自启：改用启动文件夹快捷方式（非提权）。
- 中文输入错字：键盘事件被输入法改写，改为剪贴板粘贴注入，并保留按键回退。

## 0.1.0 — 2026-10-24

首次公开发布：7 个 `vm_*` 工具、访客代理（反向隧道）、宿主隧道、坐标标尺、合成光标、用例与文档。
