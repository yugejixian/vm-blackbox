# 发布清单（vm-tester）

发布前逐项打勾；**没打勾就别发**。

## 一、包本身

- [x] `README.md`（装法、依赖、工具表、已知限制）
- [x] `LICENSE`（MIT）
- [x] `package.json` 里有 `license` / `keywords` / `description`
- [ ] **改包名**：`@local/vm-tester` 不能上公开 npm，建议 `dsh-vm-tester` 或 `@<你的scope>/dsh-vm-tester`
- [ ] **去掉 `"private": true`**（现在保留是为了防手滑误发）
- [ ] 版本号按 semver 走（首发 `0.1.0` 可以）
- [ ] （可选）`repository` / `homepage` / `bugs` 指向仓库
- [ ] `npm pack --dry-run` 看一眼包内容：应当只有 `index.js`、`cordis.patch.yml`、`locale/*.json`、`icon.svg`（`files` 已限制），README/LICENSE 会自动带上

## 二、仓库结构（建议）

这个 npm 包只是"遥控器"，虚拟机侧的东西体积大、和平台绑定，**不进 npm 包但要进仓库**：

```
repo/
├─ packages/vm-tester/        # ← 这个 npm 包
├─ agent/                     # QaTunnel（访客代理）、tunnel-host.js、Calibrate
├─ scripts/                   # build-vm.ps1、vm-ui.ps1、check-run.ps1、clean-shots.ps1
├─ cases/                     # 示例用例
├─ docs/handoff.md            # 部署与踩坑
└─ docs/handoff.md 里沉淀的「已知限制」也要同步进 README
```

## 三、发布前必须做的验证（复盘里写死的规矩）

- [ ] 复制一个**隔离 profile**（不要动日常用的那个），只启用 base + web-app + vm-tester
- [ ] 在隔离 profile 里完整走一遍：安装 → 启动 → `vm_status` → 卸下 → 再启动
- [ ] 确认「已安装插件」页能看到它、能移除
- [ ] 确认设置仍可保存、不出现新手向导
- [ ] 把这套流程写进 CI 或至少写成一个脚本，别人提 issue 时能复现

> 背景：作者本机在 `install_bundle` 时遇到 pnpm 的 `EPERM`（`@img/sharp-win32-x64`），
> 上千个包被移进 `node_modules/.ignored`，profile 被打回新手向导。**不要假设安装是安全的。**

## 四、发布命令（确认上面都做完之后）

```bash
npm publish --access public
```

## 五、发布后要补的

- [ ] 在 README 顶部加徽章（npm version / license）
- [ ] 给 `vm_*` 工具写一份单独的 `docs/tools.md`（含每个工具的参数与返回）
- [ ] 用户第一次跑起来最容易卡在「虚拟机侧没部署」——README 里要有一张**排错表**：
  `vm_status` 返回 `no guest connection` 时，依次查：虚拟机是否开机 → 代理是否以计划任务运行 → 隧道端口 8890 是否在监听
