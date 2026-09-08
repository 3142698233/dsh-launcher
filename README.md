# DSH Web 后台启动器（DshTray）

> **v1.2.0** · Windows 系统托盘程序 · [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的 Web UI 常驻启动器

DshTray 是一个原生 WinForms 托盘程序，让 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的 Web UI（`dsh web`）像普通软件一样在 Windows 后台常驻运行——**没有黑窗口、开机自启、崩溃可重启、更新不打断**。

## ✨ 功能特性

| 特性 | 说明 |
| --- | --- |
| 🖥️ 托盘常驻 | 平时无任何窗口，仅任务栏右下角托盘图标（绿 = 运行中 / 灰 = 已停止） |
| 🔄 两阶段启动 | 更新与启动完全分离，**更新失败绝不影响启动**（见下方工作原理） |
| ⬆️ 自身更新检查 | 启动时后台检查 GitHub Release，发现新版气泡提示，点击提示打开下载页 |
| 🔐 Token 感知 | dsh 每次启动生成随机登录 token，托盘打开界面时自动附带，无需手动复制 |
| 🚀 快速就绪 | 直接用已安装的 dsh 程序启动服务器，不联网、不经 npx，启动仅需 3 秒左右 |
| 📁 日志完整 | 服务器输出、更新记录、操作日志全部落盘 `logs\`，可随时查看 |
| 💥 异常兜底 | 启动失败弹窗显示日志末尾；服务器崩溃气泡通知、变灰、一键重启 |
| 🔌 开机自启 | 写入当前用户注册表 `Run` 键，支持一键开关与旧版自动迁移 |
| 🔧 端口可配 | 默认 `3080`，支持 `--port` 参数自定义 |

## 🚀 快速开始

1. **安装依赖**：本机需有 [Node.js](https://nodejs.org/)（`node -v` 有版本号），并已安装 dsh：

   ```bash
   npm install -g @deepseek-ai/dsh
   ```

2. **运行托盘**：双击 `DshTray.exe`（或运行 `Start-DshTray.bat`）。首次启动会短暂显示终端窗口（检查更新 → 启动服务器），就绪后自动隐藏。

3. **打开界面**：双击托盘图标，浏览器自动打开 `http://127.0.0.1:3080`（自动附带登录 token）。

4. **（可选）开机自启**：右键托盘图标 → **开机自动启动**。

> 提示：右下角看不到图标时，点击任务栏 **「^」** 展开隐藏图标，可拖到常驻区。

## ⚙️ 工作原理：两阶段启动

dsh 通过 npm 发布且迭代很快（当前为预发布版本），"有更新时启动失败"是常见痛点。DshTray 用两阶段启动根治：

```text
启动托盘
   │
   ├─ 阶段 A：版本检查/更新（后台线程，带超时保护）
   │    本地版本 vs npm 最新版（npm view，30s 超时）
   │    ├─ 有新版 → npm install -g @deepseek-ai/dsh@最新版（180s 超时）
   │    ├─ 更新失败 → 记录日志 + 气泡提示，【继续用现有版本启动】
   │    └─ 已是最新 → 直接进入阶段 B
   │
   ├─ 阶段 B：直接启动服务器
   │    定位已安装 dsh（优先全局 npm 安装，其次 npx 缓存）
   │    node ...\@deepseek-ai\dsh\lib\bin.js web --port 3080 --no-open
   │    （不联网、不经 npx、不被缓存文件锁影响；--no-open 不弹浏览器）
   │
   ├─ 就绪检测：TCP 连接 127.0.0.1:3080（每 1.5s 轮询）
   │
   └─ 就绪后：自动隐藏终端窗口，托盘常驻后台
```

**核心设计**：阶段 A 与阶段 B 完全解耦——即使网络差、更新失败，服务器也一定用现有版本启动，只是弹气泡提示。

## 🖱️ 托盘菜单

| 菜单项 | 作用 |
| --- | --- |
| 状态 | 运行中（端口）/ 正在检查更新 / 正在启动 / 已停止 |
| 打开界面 (Open UI) | 浏览器打开 Web UI（自动附带 token） |
| 启动服务器 (Start) | 手动启动服务器 |
| 重启服务器 (Restart) | 停止后重新启动 |
| 停止服务器 (Stop) | 结束服务器进程 |
| 显示/隐藏终端窗口 | 随时查看服务器实时输出 |
| 开机自动启动 | 开关开机自启（注册表 `HKCU\...\Run\DSHWebTray`） |
| 打开日志文件夹 | 打开 `logs\` 目录 |
| 退出托盘 (Exit) | 可选择是否同时停止服务器 |

双击托盘图标 = 打开界面。

## 📂 文件说明

| 文件 | 作用 |
| --- | --- |
| **`DshTray.exe`** | **主程序**（推荐直接双击） |
| `DshTray.cs` | 主程序源码（单文件 C#，约 800 行） |
| `Start-DshTray.bat` | 便捷启动入口 |
| `README.md` | 本文档 |
| `LICENSE` | MIT 许可证 |

运行时自动生成的目录：

| 路径 | 内容 |
| --- | --- |
| `logs\` | `dsh-stdout.log`（服务器输出）、`dsh-update.log`（更新记录）、`tray.log`（托盘日志）、`dsh-server.pid`（进程号）、`start-dsh.cmd` / `update-run.cmd` / `capture.cmd`（实际执行的命令脚本） |
| `_archive\` | 与程序无关的历史文件（调试记录等），可删除 |

## 🔧 配置

- **修改端口**：`DshTray.exe --port 8080`；或在 `DshTray.cs` 中修改默认值后重新编译。
- **端口占用**：端口已有 DSH Web 在运行时，托盘提示"已在运行"，不会重复启动；"停止服务器"可结束占用该端口的 node 进程。

## 🛠️ 开发

重新编译（需 .NET Framework 4.x 的 `csc.exe`）：

```bash
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:DshTray.exe DshTray.cs
```

## ❓ 常见问题

**Q: 浏览器提示 "dsh web authentication required; reopen the URL printed by dsh web."？**
A: dsh 每次启动都会生成随机登录 token，必须用带 token 的 URL 才能访问。v1.1.0 起托盘打开界面已自动附带 token；若仍遇到（如服务器为手动启动），从 `logs\dsh-stdout.log` 复制最新一行 `http://127.0.0.1:3080/?token=...` 打开。

**Q: 有更新时无法正常启动怎么办？**
A: 已根治：更新和启动完全分离。即使网络差、更新失败，服务器也会用现有版本启动，仅弹气泡提示。手动更新：`npm install -g @deepseek-ai/dsh@latest`。

**Q: 平时会有黑窗口吗？**
A: 平时没有。仅启动/更新期间短暂显示终端窗口（可看到进度），就绪后自动隐藏；托盘菜单可随时"显示/隐藏终端窗口"。

**Q: 双击 exe 后没看到图标？**
A: 查看 `logs\tray.log` 是否有报错；确认本机已安装 Node.js。

**Q: 如何完全退出？**
A: 右键托盘图标 → 退出托盘，可选择是否同时停止服务器。

**Q: 如何取消开机自启？**
A: 右键取消勾选"开机自动启动"；或删除注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `DSHWebTray` 键。

## 📄 许可证

[MIT](LICENSE) © 2026 Dong

> 本工具是独立开发的第三方启动器，与 DeepSeek AI 无隶属关系。它启动的 DeepSeek Harness（`@deepseek-ai/dsh`）是 [DeepSeek AI 的开源项目](https://github.com/deepseek-ai/deepseek-harness)，遵循其自身许可证。
