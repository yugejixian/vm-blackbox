# vm-blackbox — 在虚拟机里像人一样测软件

> 让 AI 测试员只靠**屏幕像素 + 鼠标键盘**操作一台真实的 Windows 虚拟机，判定必须有截图为证。

## 为什么

AI 跑自动化测试最容易失效的地方不是"点不准"，而是**它能读到日志、源码、内部状态，于是"知道"自己没错**——测试就退化成了自我确认。

这个项目把测试员扔进真实虚拟机，只给它截图和键鼠：看不到日志、进程、注册表，也够不到宿主。结论只能靠像素，而像素可以被独立复核。这就是"真实"的来源。

## 组成

| 目录 | 内容 |
|---|---|
| [`packages/vm-tester`](packages/vm-tester) | **npm 包**：DSH 插件，暴露 `vm_status / vm_screenshot / vm_click / vm_type / vm_key / vm_key_basic / vm_wait` |
| [`agent/`](agent) | 虚拟机内代理 `QaTunnel.exe`（截屏+SendInput，反向连宿主）、宿主隧道 `tunnel-host.js`、标定工具 `Calibrate.exe` |
| [`scripts/`](scripts) | `build-vm.ps1` 一键重建虚拟机、`vm-ui.ps1` 窗口/无窗口切换、`check-run.ps1` 运行体检、`clean-shots.ps1` 清截图、`VmWatch.exe` 桌面围观小窗 |
| [`cases/`](cases) | 八条示例用例（记事本/计算器/安装/取消/卸载/游戏/资源管理器/浏览器） |
| [`docs/`](docs) | `handoff.md` 部署与踩坑、`packaging.md` 打包清单、`PUBLISHING.md` 发布检查表 |
| [`examples/`](examples) | 测试员提示词模板、执行报告模板 |

## 架构

```
DSH 测试员 ──工具调用──▶ vm-tester 插件 ──HTTP :8890──▶ tunnel-host.js (宿主)
                                                              ▲
                                                              │ 反向隧道 :8801
                                                              │
                                                     QaTunnel.exe (虚拟机内)
                                                              │ SendInput / GDI
                                                              ▼
                                                     Windows 虚拟机（真实桌面）
```

虚拟机主动外连宿主，所以**不需要给虚拟机开入站端口**。

## 快速开始

1. 装 VirtualBox 7.x，准备一个 Windows 11 中文 ISO；
2. 跑 `scripts/build-vm.ps1 -Iso <你的 iso>`——建虚拟机、无人值守装系统、装 Guest Additions（含 3D）、部署代理、打基线快照；
3. 宿主启动 `agent/tunnel-host.js`（Node）；
4. 把 `packages/vm-tester` 装进 DSH profile；
5. 需要看画面：双击 `scripts/VmWatch.exe`，或开 `http://127.0.0.1:8890/viewer`；
6. 需要亲自操作：`scripts/vm-ui.ps1 -Mode show`。

详细步骤与全部踩过的坑见 [`docs/handoff.md`](docs/handoff.md)。

## 状态

八条示例用例在中文 Windows 11 上全部 PASS（含一条商业 Unity 游戏）。已知限制、帧率、坐标行为都写在 `docs/handoff.md` 的「踩过的坑」里。

## 许可与免责

MIT，见 [`LICENSE`](LICENSE)。

**使用前请阅读 [`DISCLAIMER.md`](DISCLAIMER.md)**：软件许可、EULA、隐私与合规责任由使用者自行承担。
