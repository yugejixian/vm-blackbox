# 傻瓜包清单（未来打包用）

## 一、进包的东西

| 文件 / 目录 | 说明 |
|---|---|
| `agent\QaTunnel.exe` + `QaTunnel.cs` | 访客代理（反向隧道 + 截图标尺 + 点击准星），含源码 |
| `agent\RulerOverlay.exe` + `.cs` | 宿主侧兜底：3D 窗口抓不到时的帧缓冲叠加 |
| `agent\Calibrate.exe` + `.cs` | 全屏标定工具：验证「请求坐标 → 实际落点」是否 1:1（换机器/换显卡驱动后跑一次，30 秒） |
| `scripts\VmWatch.exe` + `.cs` | 桌面围观小窗（置顶、可缩放、带帧率与连接状态），不依赖 DSH |
| `agent\tunnel-host.js` | 宿主隧道（8801 反向口 + 8890 HTTP：`/shot` `/live` `/viewer` `/click` …） |
| `agent\install-task2.bat` / `uac-off.bat` | 访客内安装脚本（提权计划任务 / 关 UAC 弹窗） |
| 仓库根目录（`index.js`、`package.json`、`cordis.patch.yml`、`locale/`） | DSH 插件：`vm_*` 工具 |
| `tool-gate`（另装） | 工具白名单插件，按挡位锁工具 |
| `presets\vm-naive.patch.yml`、`vm-smart.patch.yml` | 两个智能档位的预设 |
| `cases\*.md` | 用例（001–006） |
| `scripts\build-vm.ps1` | 一键重建虚拟机 |
| `docs\handoff.md` | 交接说明：时序、坑、铁律 |
| `docs\packaging.md` | 本文件 |

## 二、绝对不能进包

| 东西 | 原因 |
|---|---|
| Windows ISO | 版权，且 8.5 GB |
| `Unattended-*.xml` / `VBOXPOST.CMD` | **含明文密码**和本机 UUID；每次安装会重新生成 |
| 虚拟机快照 / vdi | 体积巨大，且含已保存的访客凭据 |
| 访客密码（示例值） | 只在构建时用；包里应改成首次运行随机生成并写入配置 |

## 三、使用者需要自备

1. VirtualBox 7.x（Windows 宿主已验证）
2. 官方 Windows 11 ISO（中文专业版）
3. DSH（本测试台寄生在 DSH 的插件机制上）
4. Python/Node 不必须——除 `tunnel-host.js` 需要 Node（本机用 `D:\nodejs\node.exe`）

## 四、打包时最容易翻车的三处

1. **`install_bundle` 不能用于本方案**：在这台机器上它会让 pnpm 把上千个包挪进 `node_modules\.ignored` 并 EPERM 失败（复现两次）。插件必须**手动安装**：把包复制到 `<profile>\node_modules\@local\<name>\`，再往 `cordis.patch.yml` 追加一条 `- insert: - id: <name> / name: '@local/<name>'`，最后重启 DSH。
2. **访客代理必须高完整性**：计划任务要 `/RL HIGHEST /IT`（少了 `/IT` 会跑进非交互会话，代理能连上但看不见桌面）。
3. **安装完必须摘 ISO + `boot1=disk`**：否则下次重启又从光盘进安装程序，把已装系统覆盖掉（踩过一次，损失一台虚拟机）。
