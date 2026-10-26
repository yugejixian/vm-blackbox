# 快速开始（保姆级）

> 目标：从「装好插件」到「第一次成功截图并点击」，全程约 30–60 分钟（大头是装 Windows 虚拟机）。
> 全程不需要你懂代码，照着复制粘贴即可。**遇到的每个坑都有对应小节。**

---

## 0. 先看这个：你需要准备什么

| 项 | 要求 | 说明 |
|---|---|---|
| 宿主机 | Windows 10/11 | 插件和隧道都跑在宿主上 |
| VirtualBox | **7.x** | 6.x 截图 API 不同，不保证可用 |
| 虚拟机 | 一台 **Windows 10/11** | 中英文都行；推荐中文（输入法测试更真实） |
| Node.js | **20+** | 跑隧道用；DSH 自带的 Node 也可以 |
| 磁盘 | ≥ 40 GB 空闲 | 虚拟机系统盘 |
| 权限 | 管理员 | 网关/防火墙/装系统都需要 |

> 虚拟机**不需要** GPU、不需要联网、不需要装任何测试框架。它就是一个「能开机的 Windows」。

---

## 1. 安装插件

在 DSH 里任选一种（推荐第一种）：

```sh
# ① 从 npm 装（最稳）
dsh plugin add dsh-vm-tester

# ② 从 GitHub 仓库装（拿到的文件最全，含本指南与全部脚本）
dsh plugin add github:yugejixian/vm-blackbox
```

装完**重启 DSH**（完全退出再打开）。

### 验证
在 DSH 里问一句「列出你的 vm 工具」，应该能看到 8 个：`vm_status`、`vm_screenshot`、`vm_click`、`vm_type`、`vm_type_secret`、`vm_key`、`vm_key_basic`、`vm_wait`。

此时直接调 `vm_status` 会报「隧道连不上」——**这是正常的**，因为虚拟机和隧道还没起步。继续往下。

---

## 2. 准备一台 Windows 虚拟机

### 方案 A：用脚本一键建（推荐，需自备 Windows ISO）

脚本会：建虚拟机（EFI + vTPM + 4G 内存 / 2 核 / 64G 动态盘 + NAT）→ 无人值守装 Win11 中文专业版 → 装 Guest Additions → 投递访客代理 → 关 UAC → 打基线快照。

```powershell
cd <插件目录>            # 例如 C:\Users\你\.dsh\profiles\desktop\node_modules\dsh-vm-tester
powershell -ExecutionPolicy Bypass -File .\scripts\build-vm.ps1 -Iso "D:\你的win11.iso"
```

> ISO 去微软官网下（「Windows 11 磁盘映像 (ISO)」），中文版选 `zh-cn`。

### 方案 B：手动建（不想跑脚本时）

1. VirtualBox → 新建 → 类型 **Microsoft Windows / Windows 11 (64-bit)**；
2. 内存 ≥ 4096 MB、CPU ≥ 2 核、硬盘 ≥ 64 GB（动态分配即可）；
3. 勾选 **EFI**（设置 → 系统 → 启用 EFI），然后正常装系统；
4. 装完在虚拟机里装 **Guest Additions**（设备 → 插入增强功能光盘 → 运行）；
5. 控制面板里把 **UAC 调到最低**（用户账户控制 → 从不通知）；
6. 打开「自动登录」：`netplwiz` → 取消勾选「要使用本计算机，用户必须输入用户名和密码」。

> 第 5、6 步很重要：不关 UAC，点击安装程序会弹窗卡死流程；不开自动登录，重启后没人登录，代理就不会启动。

---

## 3. 把「访客代理」放进虚拟机

访客代理是虚拟机里的小程序 `QaTunnel.exe`：它开机自启，反向连宿主，负责截屏和模拟鼠标键盘。

### 3.1 复制文件进去

二选一：

- **拖拽**：在文件管理器里把 `agent/QaTunnel.exe` 拖进虚拟机窗口的 `C:\qa\`（先建好这个目录）；
- **命令行**（虚拟机开机并登录后用）：

```powershell
& "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe" guestcontrol 你的虚拟机名 copyto `
  --username 访客用户名 --password 访客密码 `
  "<插件目录>\agent\QaTunnel.exe" "C:\qa\QaTunnel.exe"
```

### 3.2 设成开机自启（必做）

在虚拟机里，用记事本新建 `C:\qa\启动代理.ps1`，粘贴：

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $startup 'QaTunnel Autostart.lnk'))
$lnk.TargetPath = 'C:\qa\QaTunnel.exe'
$lnk.WorkingDirectory = 'C:\qa'
$lnk.WindowStyle = 7
$lnk.Save()
```

然后在虚拟机里右键该文件 →「使用 PowerShell 运行」。之后**每次开机登录都会自动启动代理**。

> 也可以直接跑仓库里的 `scripts/mk-startup.ps1`（作用相同）。

### 3.3 重启虚拟机验证

重启后**先不要操作虚拟机**，回到宿主继续第 4 步。虚拟机里的代理会自动连回来。

---

## 4. 在宿主启动「隧道」

隧道是宿主上的一个小 HTTP 服务（默认 `127.0.0.1:8890`），插件只跟它说话，它再跟虚拟机里的代理说话。

```powershell
cd "<插件目录>\agent"
node tunnel-host.js
```

看到 `listening on 8890` 之类的输出就是成功。**这个窗口别关**。

### 设成开机自启（强烈建议）

把下面内容存为 `tunnel-autostart.ps1`，再建个快捷方式丢进 `shell:startup`：

```powershell
$node = (Get-Command node).Source
Start-Process -FilePath $node -ArgumentList '<插件目录>\agent\tunnel-host.js' -WindowStyle Hidden
```

---

## 5. 配置插件（两行）

打开 DSH 的插件配置（profile 里的 `cordis.patch.yml`，或插件管理页），把这两项改成你的实际情况：

```yaml
- id: vm-tester
  config:
    tunnelUrl: 'http://127.0.0.1:8890'      # 一般不用改
    shotsDir: 'C:\Users\你\Pictures\vmshots' # 截图保存目录，改成你喜欢的路径
```

改完**重启 DSH**。

---

## 6. 冒烟测试（三步验收）

1. **连通性**：调 `vm_status` → 应返回 `connected: true` 和分辨率；
2. **看得见**：调 `vm_screenshot` → 应看到虚拟机桌面，且图上带坐标标尺；
3. **点得动**：调 `vm_click(x, y)` 点「开始」按钮 → 再 `vm_screenshot` 看菜单是否展开。

三步都过 → 环境就绪。可以跑现成用例了（`cases/case-001-notepad.md`：打开记事本输入中文再核对）。

---

## 7. 常见问题

| 现象 | 原因 | 解决 |
|---|---|---|
| `vm_status` 返回 `connected: false` | ①隧道没启动 ②访客代理没运行 ③虚拟机没登录 | 依次检查：宿主有没有跑 `tunnel-host.js`；虚拟机里 `tasklist` 有没有 `QaTunnel.exe`；确认自动登录开着并重启 |
| 截图全黑 / 是旧画面 | 3D 或 GPU 合成窗口 | 插件会自动切宿主抓帧；仍黑就把虚拟机显示设置里的「图形控制器」改为 **VBoxSVGA** |
| 点击位置总偏一点 | 缩略图与原生像素比例不同 | **看截图上的坐标标尺**，用标尺读原生像素再点；每次窗口大小变化后重新截图 |
| 中文输入变成「中中中中」 | 输入法拦截了模拟按键 | 已改为剪贴板粘贴，确保虚拟机能访问剪贴板（Guest Additions 已装即可） |
| 安装程序弹 UAC 卡住 | UAC 没关 | 重做第 2 步第 5 条 |
| 重启后连不上 | 代理没设开机自启 | 重做 3.2，或检查启动文件夹里有没有那个 `.lnk` |

---

## 8. 安全建议（如果你要测带密码的功能）

1. **永远在虚拟机里测，别在宿主机上点真实账号**；
2. 需要登录时，用 `vm_type_secret("名字")` 注入密码——先用 `scripts/vault-set.ps1` 把密码存进宿主凭据库（DPAPI 加密），**明文不会进入对话上下文**；
3. 注入后插件会自动把密码框和标题栏涂黑 180 秒，截图里看不到明文；
4. 测试前给虚拟机打快照，测完回滚，登录态和残留文件一起消失；
5. 详见 `docs/handoff.md` 的「敏感数据与登录场景」一节。

---

## 9. 想看得见它在干什么？

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\VmWatch.exe
```

桌面会弹一个实时小窗（约 2 帧/秒），能直接看到虚拟机的每一帧。嫌挡事就关掉。

---

遇到本文档没覆盖的问题：开 issue 并附上 **`vm_status` 的返回**、**隧道窗口的输出**、**虚拟机里 `tasklist | findstr QaTunnel` 的结果**——这三样能定位 90% 的问题。
