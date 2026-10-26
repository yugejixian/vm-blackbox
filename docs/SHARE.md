# 分享版：不走插件市场的安装与配置

> 这份文档是给朋友的：**不需要经过插件市场、不需要市场审核**，直接从 GitHub / npm / Release 装。
> 全部命令可直接复制。全程约 10 分钟（虚拟机已有时）；从零装 Windows 虚拟机约 40 分钟。

---

## 一、下哪一种（三选一）

| 方式 | 命令 | 说明 |
|---|---|---|
| **A. Release 包（推荐，不依赖 npm）** | 见下方分步 | 一个 80 KB 的 `.tgz`，包含插件 + 部署脚本 + 文档 |
| **B. npm** | `dsh plugin add dsh-vm-tester` | 需要能访问 npm |
| **C. GitHub 仓库** | `dsh plugin add github:yugejixian/vm-blackbox` | 需要能访问 GitHub |

### A 方式分步

1. 浏览器打开下载页（直接下载，无需登录）：
   <https://github.com/yugejixian/vm-blackbox/releases/latest>
2. 下载 **`dsh-vm-tester-0.1.3.tgz`**（80 KB）；
3. 装进 DSH：

   ```sh
   dsh plugin add C:\Users\你\Downloads\dsh-vm-tester-0.1.3.tgz
   ```

4. 完全退出 DSH 再打开（让插件加载）。

### B / C 方式

```sh
dsh plugin add dsh-vm-tester
# 或
dsh plugin add github:yugejixian/vm-blackbox
```

装完同样**完全重启 DSH**。

---

## 二、前置条件

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 |
| VirtualBox | **7.x**（<https://www.virtualbox.org/wiki/Downloads>） |
| 虚拟机 | 一台 **Windows 10/11**（中英文都行），已开机并登录桌面 |
| Node.js | **20+**（<https://nodejs.org/>，安装后 `node -v` 能看到版本） |

> 虚拟机不需要 GPU、不需要联网、不需要装任何测试框架。
> 没有现成虚拟机？用仓库里的脚本建：`scripts\build-vm.ps1 -Iso "你的win11.iso"`（需自备 ISO）。

---

## 三、一键部署（一条命令）

先找到插件目录（任选其一）：

- **用 A 方式装的**：解压那个 tgz 到一个文件夹，就得到插件目录；
- **用 B/C 方式装的**：目录在 `C:\Users\你\.dsh\profiles\<profile>\node_modules\dsh-vm-tester`
  （`<profile>` 一般是 `desktop` 或 `web`）。

在这个目录里打开 PowerShell，执行（把三个参数换成你的）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\quick-setup.ps1 `
  -VmName "你的虚拟机名" `
  -GuestUser "虚拟机里的用户名" `
  -GuestPass "虚拟机里的密码"
```

**虚拟机名在哪看**：VirtualBox 主界面左侧列表里的名字（如 `qa-win11-zh`）。

脚本会自动做完这些（可重复执行、不需要管理员权限）：

1. 检查 VirtualBox / 虚拟机 / Node.js；
2. 把访客代理 `QaTunnel.exe` 投递进虚拟机 `C:\qa\`；
3. 设置开机自启，并立刻启动代理；
4. 在宿主后台启动隧道（默认 `127.0.0.1:8890`）；
5. 等待连通，并打印分辨率和截图目录。

成功后你会看到：

```
================ 部署完成 ================
  虚拟机     : qa-win11-zh
  访客用户   : qa
  分辨率     : 1280x720
  截图目录   : C:\Users\你\AppData\Local\dsh-vm-tester\shots
  接下来     : 重启 DSH，然后调用 vm_status / vm_screenshot 验证
==========================================
```

---

## 四、验证（三步）

在 DSH 里依次让 agent 调用：

| 步骤 | 工具 | 期望结果 |
|---|---|---|
| 1 | `vm_status` | `connected: true` + 分辨率 |
| 2 | `vm_screenshot` | 看到虚拟机桌面，图上带坐标标尺 |
| 3 | `vm_click` 点「开始」再 `vm_screenshot` | 开始菜单展开 |

三步都过 = 环境就绪，可以让 agent 跑测试了。

---

## 五、常见问题

| 现象 | 原因 / 解决 |
|---|---|
| `vm_status` 报隧道连不上 | 隧道没启动：重跑 `quick-setup.ps1`；或看虚拟机里 `QaTunnel` 进程是否在跑 |
| 重启电脑后连不上 | 访客代理没自启：重跑脚本（会重设自启）。注意**虚拟机必须自动登录**，否则开机不会跑自启项 |
| 截图全黑 / 是旧画面 | VirtualBox 设置 → 显示 → 图形控制器改成 **VBoxSVGA** |
| 点击位置总是偏 | 看截图上的**坐标标尺**读原生像素；窗口大小变了就重新截图 |
| 中文打成「中中中中」 | 确认虚拟机装了 **Guest Additions**（设备 → 插入增强功能光盘） |
| 提示缺 `agent\QaTunnel.exe` | 装的是旧 npm 版本；请用 A/C 方式（含 agent 目录） |

更详细的部署与排错：仓库里的 [`docs/QUICKSTART.md`](https://github.com/yugejixian/vm-blackbox/blob/main/docs/QUICKSTART.md)。

---

## 六、它是什么（一句话给朋友）

让 AI 在**一台真实的 Windows 虚拟机里像人一样测软件**：只看截图、只点鼠标键盘，判定必须由像素支撑，而不是读日志或源码。
虚拟机是隔离的，测崩了回滚快照即可；带密码的场景可以用内置凭据库注入，密码不会进入 AI 的上下文。
