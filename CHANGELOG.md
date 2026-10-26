# Changelog

## Unreleased

### Added
- `docs/QUICKSTART.md`：保姆级部署流程（装插件 → 准备虚拟机 → 部署代理 → 启动隧道 → 冒烟测试 → 排错）。
- npm 包现在包含 `agent/`、`scripts/`、`docs/`、`assets/`，从 npm 安装也能拿到隧道与访客代理。

### Changed
- 默认截图目录不再写死 `D:\null_work\shots`，改为 `%LOCALAPPDATA%\dsh-vm-tester\shots`。
- `cordis.patch.yml` 不再写死 `shotsDir`（留空即用默认值）。
- `scripts/build-vm.ps1` 的 `AgentDir`/`BaseFolder` 改为相对仓库与用户目录，换机器不用改脚本。

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
