// =====================================================================
//  DshTray.cs — DSH Web 后台启动器（系统托盘图标版，原生 WinForms）
//  编译：csc.exe /nologo /target:winexe /out:DshTray.exe DshTray.cs
//  运行：双击 DshTray.exe —— 无任何窗口
//  启动流程：
//    1) 启动/更新服务器时显示一个终端（黑窗口），可看到版本检查/更新进度与错误
//    2) 服务器就绪（端口开始监听）后，终端窗口自动隐藏，服务器继续后台运行
//    3) 输出实时写入 logs\dsh-stdout.log；启动失败会弹窗显示错误
//  版本：1.2.0.0（真实版本对比更新 + 全局安装定位 + --no-open + 托盘自身更新检查）
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DSH Web Tray Launcher")]
[assembly: AssemblyProduct("DshTray")]
[assembly: AssemblyDescription("DSH Web 后台启动器（系统托盘）")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0.0")]

namespace DshWebTray
{
    internal static class Program
    {
        private static int Port = 3080;
        private static string Url;
        private static string LogDir, StdoutLog, UpdateLog, PidFile, TrayLog;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DSHWebTray";

        // 托盘自身更新检查（GitHub Release）
        private const string GithubRepo = "3142698233/dsh-launcher";
        private const string GithubReleaseApi = "https://api.github.com/repos/3142698233/dsh-launcher/releases/latest";
        private const string GithubReleasePage = "https://github.com/3142698233/dsh-launcher/releases/latest";
        private static string _latestReleaseTag; // 检测到的新版本 tag，供气泡点击打开下载页

        private static Mutex _mutex;
        private static NotifyIcon _notify;
        private static System.Windows.Forms.Timer _timer;
        private static ToolStripMenuItem _itemStatus, _itemStart, _itemRestart, _itemStop, _itemAuto, _itemConsole;
        private static Icon _iconRunning, _iconStopped;
        private static bool _wasRunning;
        private static readonly object LogLock = new object();

        // 服务器进程与启动状态
        private static Process _serverProc;
        private static bool _startingUp;      // 正在启动（阶段 A 或 B）
        private static bool _updating;        // 阶段 A：版本检查/更新中
        private static bool _updatePhaseDone; // 阶段 A 已完成（后台线程置位）
        private static string _updateResultMsg; // 阶段 A 结果消息
        private static bool _consoleHidden;   // 终端窗口已自动隐藏
        private static IntPtr _consoleHwnd;   // 终端窗口句柄
        private static bool _failureNotified; // 启动失败弹窗只弹一次

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleTitle(string title);
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                ParseArgs(args);

                Url = "http://127.0.0.1:" + Port;
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                LogDir = Path.Combine(dir, "logs");
                Directory.CreateDirectory(LogDir);
                StdoutLog = Path.Combine(LogDir, "dsh-stdout.log");
                UpdateLog = Path.Combine(LogDir, "dsh-update.log");
                PidFile = Path.Combine(LogDir, "dsh-server.pid");
                TrayLog = Path.Combine(LogDir, "tray.log");

                _mutex = new Mutex(false, "DSHWebTray_" + Port);
                if (!_mutex.WaitOne(0, false))
                {
                    MessageBox.Show("DSH Web 托盘（端口 " + Port + "）已在运行。", "DSH Web",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // GitHub API 要求 TLS 1.2+，而 .NET 4.0 默认仅 TLS 1.0（枚举值 3072 = Tls12）
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }

                _iconRunning = CreateIcon(Color.FromArgb(30, 168, 88));
                _iconStopped = CreateIcon(Color.FromArgb(140, 140, 140));

                BuildTray();
                MigrateAutoStart();

                Log("===== DSH Web 托盘启动（端口 " + Port + "）v" + GetVersion() + " =====");
                StartServer();
                _timer.Start();
                UpdateStatus();

                // 后台检查 GitHub Release 是否有托盘新版本（不阻塞启动，失败静默）
                Thread updater = new Thread(CheckGithubReleaseUpdate);
                updater.IsBackground = true;
                updater.Start();

                Application.Run();

                _notify.Visible = false;
                _notify.Dispose();
                try { FreeConsole(); } catch { } // 释放启动用的终端窗口
                try { _mutex.ReleaseMutex(); } catch { }
            }
            catch (Exception ex)
            {
                try { Log("托盘程序异常: " + ex); } catch { }
                MessageBox.Show("DSH Web 托盘发生错误：\n" + ex.Message, "DSH Web",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ==================== 参数 ====================
        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i].ToLower() == "-port") && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], out p) && p > 0) Port = p;
                }
            }
        }

        // ==================== 托盘界面 ====================
        private static void BuildTray()
        {
            _notify = new NotifyIcon();
            _notify.Visible = true;

            var menu = new ContextMenuStrip();

            _itemStatus = new ToolStripMenuItem("状态：检测中...") { Enabled = false };
            menu.Items.Add(_itemStatus);
            menu.Items.Add(new ToolStripSeparator());

            var itemOpen = new ToolStripMenuItem("打开界面 (Open UI)");
            itemOpen.Click += (s, e) => { try { Process.Start(GetUiUrl()); } catch { } };
            menu.Items.Add(itemOpen);

            _itemStart = new ToolStripMenuItem("启动服务器 (Start)");
            _itemStart.Click += (s, e) => StartServer();
            menu.Items.Add(_itemStart);

            _itemRestart = new ToolStripMenuItem("重启服务器 (Restart)");
            _itemRestart.Click += (s, e) => { StopServer(); StartServer(); };
            menu.Items.Add(_itemRestart);

            _itemStop = new ToolStripMenuItem("停止服务器 (Stop)");
            _itemStop.Click += (s, e) => StopServer();
            menu.Items.Add(_itemStop);

            _itemConsole = new ToolStripMenuItem("显示/隐藏终端窗口");
            _itemConsole.Click += (s, e) => ToggleConsoleWindow();
            menu.Items.Add(_itemConsole);

            menu.Items.Add(new ToolStripSeparator());

            _itemAuto = new ToolStripMenuItem("开机自动启动");
            _itemAuto.Click += (s, e) => { SetAutoStart(!IsAutoStartEnabled()); UpdateStatus(); };
            menu.Items.Add(_itemAuto);

            var itemLogs = new ToolStripMenuItem("打开日志文件夹");
            itemLogs.Click += (s, e) => { try { Process.Start("explorer.exe", LogDir); } catch { } };
            menu.Items.Add(itemLogs);

            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出托盘 (Exit)");
            itemExit.Click += (s, e) => ExitTray();
            menu.Items.Add(itemExit);

            _notify.ContextMenuStrip = menu;
            _notify.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { try { Process.Start(GetUiUrl()); } catch { } }
            };

            // 气泡点击：若是"发现新版本"提示，则打开 GitHub Release 下载页
            _notify.BalloonTipClicked += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_latestReleaseTag))
                {
                    try { Process.Start(GithubReleasePage); } catch { }
                }
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1500;
            _timer.Tick += (s, e) => UpdateStatus();
        }

        private static Icon CreateIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(color))
                        g.FillEllipse(brush, 1, 1, 14, 14);
                    using (var font = new Font("Arial", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var white = new SolidBrush(Color.White))
                    {
                        var sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("D", font, white, new RectangleF(0, 0, 16, 16), sf);
                        sf.Dispose();
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void UpdateStatus()
        {
            bool running = TestServerUp();

            // 启动中：保持终端窗口标题（防止被 cmd/powershell 覆盖）
            if (_startingUp)
            {
                try { SetConsoleTitle("DSH Web starting - window auto-hides when ready"); } catch { }
            }

            // 阶段 A 结束 → 进入阶段 B（无论更新成功与否都用已装版本启动，避免"更新失败就起不来"）
            if (_updating && _updatePhaseDone)
            {
                _updating = false;
                string msg = _updateResultMsg ?? "版本检查完成";
                if (msg.IndexOf("失败", StringComparison.Ordinal) >= 0 || msg.IndexOf("异常", StringComparison.Ordinal) >= 0)
                {
                    ShowBalloon("DSH Web 更新提示", msg + "，将用现有版本启动，不影响使用。");
                }
                StartServerPhase2();
            }

            // 阶段 A 期间若端口意外就绪（其他实例接管），直接结束启动流程
            if (_updating && running)
            {
                _updating = false;
                _startingUp = false;
                _updatePhaseDone = true;
                Log("更新期间检测到端口 " + Port + " 已有服务，停止启动流程");
            }

            // 阶段 B：服务器就绪后自动隐藏终端窗口
            if (_startingUp && !_updating && running)
            {
                HideConsoleWindow();
                if (_consoleHidden) _startingUp = false;
            }

            // 阶段 B 启动失败检测：进程已退出但端口始终未起来
            if (_startingUp && !_updating && !running && _serverProc != null && _serverProc.HasExited && !_failureNotified)
            {
                _failureNotified = true;
                _startingUp = false;
                Log("服务器启动失败：进程已退出（端口 " + Port + " 未监听）");
                string tail = GetLogTail(StdoutLog, 25);
                if (tail.Length == 0) tail = "(终端无输出，常见原因：dsh 未正确安装，可先手动运行 npx --yes @deepseek-ai/dsh 完成安装后重试)";
                MessageBox.Show(
                    "DSH Web 启动失败：服务器进程已退出。\n\n—— 终端输出末尾 ——\n" + tail + "\n\n完整输出见：" + StdoutLog,
                    "DSH Web", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 失败后隐藏终端窗口（进程已退出，窗口已无内容）
                try
                {
                    IntPtr h = GetConsoleWindow();
                    if (h != IntPtr.Zero) { ShowWindow(h, SW_HIDE); _consoleHidden = true; }
                }
                catch { }
            }

            if (_wasRunning != running)
            {
                if (running)
                {
                    Log("检测到服务器开始监听端口 " + Port);
                    ShowBalloon("DSH Web 已启动", "界面地址：" + GetUiUrl());
                }
                else
                {
                    Log("检测到服务器已停止监听端口 " + Port);
                    ShowBalloon("DSH Web 已停止", "服务器已停止运行，可右键托盘图标重新启动。");
                }
            }
            _wasRunning = running;

            _notify.Icon = running ? _iconRunning : _iconStopped;
            if (_itemStatus != null)
            {
                _itemStatus.Text = running
                    ? ("状态：运行中（端口 " + Port + "）")
                    : (_startingUp ? (_updating ? "状态：正在检查更新…" : "状态：正在启动…") : "状态：已停止");
                _itemStart.Enabled = !running && !_startingUp;
                _itemStop.Enabled = running || _startingUp;
                _itemRestart.Enabled = running || _startingUp;
                _itemConsole.Enabled = _startingUp || running;
            }
            if (_itemAuto != null) _itemAuto.Checked = IsAutoStartEnabled();
            _notify.Text = running
                ? ("DSH Web - 运行中 127.0.0.1:" + Port)
                : (_startingUp ? "DSH Web - 正在启动…" : "DSH Web - 已停止（右键菜单可启动）");
        }

        private static void ExitTray()
        {
            DialogResult choice = MessageBox.Show(
                "退出托盘程序时是否同时停止 DSH Web 服务器？\n\n选择「否」则服务器继续在后台运行。",
                "DSH Web 托盘", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            if (choice == DialogResult.Yes) StopServer();
            Log("托盘程序退出");
            Application.Exit();
        }

        // ==================== 终端窗口控制 ====================
        // 启动时分配/显示一个属于本程序的终端窗口（子进程会自动挂接到它）
        private static void ShowStartupConsole()
        {
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero) AllocConsole();
                SetConsoleTitle("DSH Web starting - window auto-hides when ready");
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, SW_SHOW);
                    _consoleHwnd = h;
                    _consoleHidden = false;
                }
            }
            catch { }
        }

        // 服务器就绪后隐藏终端窗口（进程继续后台运行）
        private static void HideConsoleWindow()
        {
            if (_consoleHidden) return;
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, SW_HIDE);
                    _consoleHwnd = h;
                    _consoleHidden = true;
                    Log("服务器已就绪，终端窗口已自动隐藏");
                }
            }
            catch { }
        }

        // 托盘菜单：显示/隐藏终端窗口（调试用）
        private static void ToggleConsoleWindow()
        {
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h == IntPtr.Zero) return;
                _consoleHwnd = h;
                bool visible = IsWindowVisible(h);
                ShowWindow(h, visible ? SW_HIDE : SW_SHOW);
                _consoleHidden = visible; // 若原本可见，则现在已隐藏
                Log(visible ? "终端窗口已隐藏（可随时重新显示）" : "终端窗口已显示");
            }
            catch { }
        }

        // ==================== 服务器管理 ====================
        // 两阶段启动：
        //   阶段 A：后台线程对比本地 dsh 版本与 npm 最新版，有新版则 npm install -g 更新
        //           （终端可见进度，超时保护；失败不影响启动）
        //   阶段 B：直接用已安装的 dsh 启动服务器（优先全局安装，其次 npx 缓存；不联网）
        private static void StartServer()
        {
            if (TestServerUp())
            {
                ShowBalloon("DSH Web 已在运行", "端口 " + Port + " 已有服务在监听，无需重复启动。");
                UpdateStatus();
                return;
            }
            try { File.Delete(PidFile); } catch { }

            // 等待旧服务器进程完全退出、文件锁释放
            Thread.Sleep(1000);

            ShowStartupConsole(); // 分配并显示终端窗口（更新与启动子进程都挂接它）

            _startingUp = true;
            _updating = true;
            _updatePhaseDone = false;
            _updateResultMsg = null;
            _consoleHidden = false;
            _consoleHwnd = IntPtr.Zero;
            _failureNotified = false;
            Log("阶段 A：检查 dsh 版本更新…");
            ShowBalloon("DSH Web 正在启动", "正在检查/更新版本（终端窗口可见），完成后将自动启动服务器并隐藏窗口。");

            Thread t = new Thread(RunUpdatePhase);
            t.IsBackground = true;
            t.Start();
            UpdateStatus();
        }

        // 阶段 A（后台线程）：对比本地版本与 npm 最新版本，必要时执行全局更新
        private static void RunUpdatePhase()
        {
            string msg;
            try
            {
                string local = GetLocalDshVersion();
                string latest = QueryLatestVersion();
                if (string.IsNullOrEmpty(latest))
                {
                    msg = "无法查询 npm 最新版本（可能网络问题），将用现有版本启动";
                }
                else if (string.IsNullOrEmpty(local) || local != latest)
                {
                    Log("阶段 A：本地版本 " + (local ?? "未安装") + "，npm 最新版本 " + latest + "，执行全局更新…");
                    int code = RunNpmInstallGlobal(latest);
                    msg = (code == 0)
                        ? "已更新到 " + latest
                        : "更新未完全成功（退出码 " + code + "，可能网络/权限问题），将用现有版本启动";
                }
                else
                {
                    msg = "已是最新版本 " + local + "，无需更新";
                }
            }
            catch (Exception ex)
            {
                msg = "版本检查异常：" + ex.Message;
            }
            _updateResultMsg = msg;
            _updatePhaseDone = true;
            Log("阶段 A 完成：" + msg);
        }

        // 读取本地已安装 dsh 的版本号（全局安装优先，其次 npx 缓存）
        private static string GetLocalDshVersion()
        {
            string globalPkg = FindGlobalDshPackageJson();
            if (globalPkg != null)
            {
                string v = ReadVersionFromPkg(globalPkg);
                if (v != null) return v;
            }
            string bin = FindDshBinInNpxCache();
            if (bin != null)
            {
                string pkg = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bin)), "package.json");
                string v = ReadVersionFromPkg(pkg);
                if (v != null) return v;
            }
            return null;
        }

        // 查询 npm 上 @deepseek-ai/dsh 的最新版本（30s 超时）
        private static string QueryLatestVersion()
        {
            string node = FindExecutable("node.exe");
            if (node == null) return null;
            string npmCli = Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(npmCli)) return null;
            string output = RunProcessCapture("\"" + node + "\" \"" + npmCli + "\" view @deepseek-ai/dsh version", 30000);
            if (string.IsNullOrEmpty(output)) return null;
            string[] lines = output.Replace("\r", "").Split('\n');
            string last = null;
            foreach (string ln in lines)
            {
                string t = ln.Trim();
                if (t.Length > 0 && char.IsDigit(t[0])) last = t;
            }
            return last;
        }

        // 执行 npm install -g @deepseek-ai/dsh@version（180s 超时，输出 Tee 到 update.log）
        private static int RunNpmInstallGlobal(string version)
        {
            string node = FindExecutable("node.exe");
            if (node == null) return -1;
            string npmCli = Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(npmCli)) return -1;
            string cmd = "\"" + node + "\" \"" + npmCli + "\" install -g @deepseek-ai/dsh@" + version;
            Log("执行更新：" + cmd);
            return RunProcessExitCode(cmd, 180000, UpdateLog);
        }

        // 运行命令并捕获输出（带超时；用于 npm view / npm prefix 等短命令）
        // 通过临时批处理执行，避免 cmd /c 嵌套引号在含空格路径下被剥离的问题
        private static string RunProcessCapture(string cmdLine, int timeoutMs)
        {
            try
            {
                string batch = "@echo off\r\n" + cmdLine + "\r\n";
                string batchPath = Path.Combine(LogDir, "capture.cmd");
                try { File.WriteAllText(batchPath, batch, Encoding.Default); } catch { }
                var psi = new ProcessStartInfo("cmd.exe", "/c call \"" + batchPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { KillTree(p.Id); } catch { }
                        Log("命令超时（" + timeoutMs + "ms）：" + cmdLine);
                        return null;
                    }
                    string outp = p.StandardOutput.ReadToEnd();
                    string errp = p.StandardError.ReadToEnd();
                    return (outp + "\n" + errp).Trim();
                }
            }
            catch (Exception ex) { Log("命令执行异常: " + ex.Message); return null; }
        }

        // 运行命令并将输出 Tee 到日志文件（带超时；用于 npm install -g 等长命令）
        private static int RunProcessExitCode(string cmdLine, int timeoutMs, string logFile)
        {
            try
            {
                string batch =
                    "@echo off\r\n" +
                    cmdLine + " 2>&1 | " + BuildTee(logFile) + "\r\n";
                string batchPath = Path.Combine(LogDir, "update-run.cmd");
                try { File.WriteAllText(batchPath, batch, Encoding.Default); } catch { }
                var psi = new ProcessStartInfo("cmd.exe", "/c call \"" + batchPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false; // 挂接到托盘分配的控制台窗口，用户可见更新进度
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { KillTree(p.Id); } catch { }
                        Log("更新命令超时（" + timeoutMs + "ms），已终止");
                        return -2;
                    }
                    try { return p.ExitCode; } catch { return -1; }
                }
            }
            catch (Exception ex) { Log("更新命令执行异常: " + ex.Message); return -1; }
        }

        // 查询 npm 全局安装根目录（npm prefix -g，15s 超时）
        private static string GetNpmGlobalPrefix()
        {
            try
            {
                string node = FindExecutable("node.exe");
                if (node == null) return null;
                string npmCli = Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npm-cli.js");
                if (!File.Exists(npmCli)) return null;
                string output = RunProcessCapture("\"" + node + "\" \"" + npmCli + "\" prefix -g", 15000);
                if (string.IsNullOrEmpty(output)) return null;
                string prefix = output.Trim();
                if (prefix.Length > 0 && Directory.Exists(prefix)) return prefix;
            }
            catch { }
            return null;
        }

        private static string FindGlobalDshPackageJson()
        {
            foreach (string root in GetDshCandidateRoots())
            {
                string cand = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "package.json");
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        private static string ReadVersionFromPkg(string pkgPath)
        {
            try
            {
                string json = File.ReadAllText(pkgPath, Encoding.UTF8);
                int i = json.IndexOf("\"version\"", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                int s = json.IndexOf('"', i + 9);
                if (s < 0) return null;
                int e = json.IndexOf('"', s + 1);
                if (e < 0) return null;
                string v = json.Substring(s + 1, e - s - 1).Trim();
                return v.Length > 0 ? v : null;
            }
            catch { return null; }
        }

        // 阶段 B：用缓存中已安装的 dsh 直接启动服务器（同一个终端窗口保持可见）
        private static void StartServerPhase2()
        {
            ProcessStartInfo psi = BuildServerStartInfo();
            if (psi == null)
            {
                _startingUp = false;
                _updating = false;
                ShowBalloon("DSH Web 启动失败", "未找到已安装的 dsh 程序，请先运行 npx @deepseek-ai/dsh 完成安装。");
                UpdateStatus();
                return;
            }

            try
            {
                var p = Process.Start(psi);
                _serverProc = p;
                try { File.WriteAllText(PidFile, p.Id.ToString()); } catch { }
                Log("阶段 B：服务器启动中 (PID " + p.Id + ")，使用已安装版本直接启动");
            }
            catch (Exception ex)
            {
                Log("服务器启动失败: " + ex.Message);
                ShowBalloon("DSH Web 启动失败", ex.Message);
            }
            UpdateStatus();
        }

        // 阶段 B：直接用已安装的 dsh 启动服务器（优先全局安装，其次 npx 缓存；不经过 npx，避免更新/锁/网络问题）。
        // 找不到时退回 npx 方式。--no-open 避免每次启动重复弹浏览器（托盘菜单自带"打开界面"）。
        private static ProcessStartInfo BuildServerStartInfo()
        {
            string node = FindExecutable("node.exe");
            if (node == null) return null;

            string dshBin = FindDshBin();
            if (dshBin != null)
            {
                string cmd = "\"" + node + "\" \"" + dshBin + "\" web --port " + Port + " --no-open";
                return BuildBatchStartInfo(cmd, StdoutLog);
            }

            // 退回：npx 启动（会尝试联网）
            string npxCli = Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npx-cli.js");
            if (!File.Exists(npxCli)) return null;
            string cmd2 = "\"" + node + "\" \"" + npxCli + "\" --yes @deepseek-ai/dsh web --port " + Port + " --no-open";
            return BuildBatchStartInfo(cmd2, StdoutLog);
        }

        // 生成批处理（可见终端 + Tee 输出到日志），通过 cmd /c call 启动
        private static ProcessStartInfo BuildBatchStartInfo(string command, string logFile)
        {
            string batch =
                "@echo off\r\n" +
                "title DSH Web starting - window auto-hides when ready\r\n" +
                command + " 2>&1 | " + BuildTee(logFile) + "\r\n";

            string batchPath = Path.Combine(LogDir, "start-dsh.cmd");
            try { File.WriteAllText(batchPath, batch, Encoding.Default); } catch { }

            var psi = new ProcessStartInfo("cmd.exe", "/c call \"" + batchPath + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false; // 挂接到 exe 分配的控制台窗口
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return psi;
        }

        // PowerShell Tee：把命令输出同时写入日志文件并回显到终端
        private static string BuildTee(string logFile)
        {
            return "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"$input | ForEach-Object { Add-Content -Path '" + logFile + "' -Value $_ -Encoding UTF8; $_ }\"";
        }

        // 找到已安装的 dsh 入口（lib/bin.js）：按候选根遍历，返回最后写入的最新版本
        // 候选根：npm 全局 prefix、%APPDATA%\npm、%LOCALAPPDATA%\npm、node.exe 同目录、npx 缓存子目录
        private static string FindDshBin()
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string root in GetDshCandidateRoots())
            {
                string cand = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(cand))
                {
                    DateTime t = File.GetLastWriteTime(cand);
                    if (t > bestTime) { bestTime = t; best = cand; }
                }
            }
            return best;
        }

        // 仅在 npx 缓存中查找 dsh 入口（供版本读取使用）
        private static string FindDshBinInNpxCache()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string npxRoot = Path.Combine(local, "npm-cache", "_npx");
                if (!Directory.Exists(npxRoot)) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string entry in Directory.GetDirectories(npxRoot))
                {
                    string cand = Path.Combine(entry, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(cand))
                    {
                        DateTime t = File.GetLastWriteTime(cand);
                        if (t > bestTime) { bestTime = t; best = cand; }
                    }
                }
                return best;
            }
            catch { return null; }
        }

        // 收集可能安装 dsh 的候选根目录（node_modules 的父级）
        private static IEnumerable<string> GetDshCandidateRoots()
        {
            var roots = new List<string>();
            try
            {
                string prefix = GetNpmGlobalPrefix();
                if (!string.IsNullOrEmpty(prefix)) roots.Add(prefix);
            }
            catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"));
            string node = FindExecutable("node.exe");
            if (node != null)
            {
                string nodeDir = Path.GetDirectoryName(node);
                if (!string.IsNullOrEmpty(nodeDir)) roots.Add(nodeDir);
            }
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string npxRoot = Path.Combine(local, "npm-cache", "_npx");
                if (Directory.Exists(npxRoot))
                    foreach (string entry in Directory.GetDirectories(npxRoot))
                        roots.Add(entry);
            }
            catch { }
            return roots;
        }

        private static void StopServer()
        {
            _startingUp = false;
            _updating = false;
            _updatePhaseDone = false;
            _updateResultMsg = null;
            _consoleHidden = false;
            _consoleHwnd = IntPtr.Zero;
            _failureNotified = true;

            bool killed = false;

            // 1) 优先按记录的 PID 结束整个进程树
            if (File.Exists(PidFile))
            {
                string saved = null;
                try { saved = File.ReadAllText(PidFile).Trim(); } catch { }
                int pid;
                if (int.TryParse(saved, out pid) && pid > 0) { KillTree(pid); killed = true; }
                try { File.Delete(PidFile); } catch { }
            }

            // 2) 兜底：仍有人监听该端口（可能是之前手动启动的实例）
            if (TestServerUp())
            {
                int listenerPid = GetListenerPid();
                if (listenerPid > 0 && listenerPid != Process.GetCurrentProcess().Id)
                {
                    try
                    {
                        Process proc = Process.GetProcessById(listenerPid);
                        if (proc.ProcessName.ToLower().Contains("node")) { KillTree(listenerPid); killed = true; }
                    }
                    catch { }
                }
            }

            Log(killed ? "服务器已停止" : "停止操作：未发现运行中的服务器");
            UpdateStatus();
        }

        private static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) p.WaitForExit(5000);
            }
            catch { }
        }

        private static int GetListenerPid()
        {
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    foreach (string line in output.Split('\n'))
                    {
                        if (line.Contains(":" + Port + " ") && line.Contains("LISTENING"))
                        {
                            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            int pid;
                            if (parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out pid)) return pid;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        private static bool TestServerUp()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(600)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        // ==================== 开机自启 ====================
        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                        Log("已启用开机自动启动");
                        ShowBalloon("已启用开机自启", "下次登录 Windows 时将自动在后台启动 DSH Web。");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                        Log("已关闭开机自动启动");
                        ShowBalloon("已关闭开机自启", "开机将不再自动启动 DSH Web。");
                    }
                }
            }
            catch (Exception ex) { Log("设置开机自启失败: " + ex.Message); }
        }

        // 旧版本写入的自启值指向 powershell，迁移为新程序
        private static void MigrateAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null) return;
                    object v = key.GetValue(RunValueName);
                    string s = v as string;
                    if (!string.IsNullOrEmpty(s) && s.IndexOf("DshTray.exe", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Log("检测到旧版开机自启项，正在迁移到新托盘程序");
                        SetAutoStart(true);
                    }
                }
            }
            catch { }
        }

        // ==================== 工具 ====================
        // 读取程序版本号（来自 AssemblyVersion）
        private static string GetVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "?" : v.ToString();
            }
            catch { return "?"; }
        }

        // ==================== 托盘自身更新检查（GitHub Release） ====================
        // 启动后延迟执行：查询 GitHub 最新 Release tag，与本地版本对比，
        // 有新版则气泡提示（点击气泡打开下载页）；网络失败/无新版时静默，不打扰用户。
        private static void CheckGithubReleaseUpdate()
        {
            try
            {
                Thread.Sleep(6000); // 延迟到启动流程基本稳定，避免与启动气泡重叠
                string tag = QueryLatestGithubTag();
                if (string.IsNullOrEmpty(tag))
                {
                    Log("GitHub 检查：无法获取最新版本（可能无网络），跳过");
                    return;
                }
                string remote = tag.TrimStart('v', 'V');
                string local = GetVersion();
                if (string.IsNullOrEmpty(remote) || CompareVersions(remote, local) <= 0)
                {
                    Log("GitHub 检查：已是最新版本（本地 " + local + " = Release " + tag + "）");
                    return;
                }
                _latestReleaseTag = tag;
                Log("GitHub 检查：发现新版本 " + tag + "（本地 " + local + "），提示用户");
                ShowBalloon("DSH Web 托盘有新版本 " + tag,
                    "点击此提示打开 GitHub Release 页面下载新版 DshTray.exe。");
            }
            catch (Exception ex)
            {
                Log("GitHub 检查异常: " + ex.Message);
            }
        }

        // 查询 GitHub Release 最新 tag（10s 超时；GitHub API 要求 User-Agent）
        private static string QueryLatestGithubTag()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(GithubReleaseApi);
                req.Method = "GET";
                req.UserAgent = "DshTray/" + GetVersion();
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    int i = json.IndexOf("\"tag_name\"", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) return null;
                    int c = json.IndexOf(':', i);
                    int s = json.IndexOf('"', c);
                    int e = json.IndexOf('"', s + 1);
                    if (s < 0 || e <= s) return null;
                    string tag = json.Substring(s + 1, e - s - 1).Trim();
                    return tag.Length > 0 ? tag : null;
                }
            }
            catch { return null; }
        }

        // 比较两个点分版本字符串（"1.1.0" 与 "1.1.0.0" 视为相等；缺失段视为 0）
        private static int CompareVersions(string a, string b)
        {
            int[] pa = ParseVersion(a), pb = ParseVersion(b);
            for (int i = 0; i < 4; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] ParseVersion(string s)
        {
            var list = new List<int>();
            foreach (string part in s.Split('.'))
            {
                int n;
                if (int.TryParse(part, out n)) list.Add(n);
            }
            return list.ToArray();
        }

        // 界面地址：dsh 每次启动都会生成随机 token，必须带 token 才能访问。
        // 从 dsh-stdout.log 中解析最新的带 token URL；找不到时退回裸地址。
        private static string GetUiUrl()
        {
            try
            {
                if (File.Exists(StdoutLog))
                {
                    string found = null;
                    foreach (string line in File.ReadAllLines(StdoutLog, Encoding.UTF8))
                    {
                        int i = line.IndexOf(Url, StringComparison.OrdinalIgnoreCase);
                        if (i < 0) continue;
                        string t = line.Substring(i).Trim();
                        if (t.IndexOf("?token=", StringComparison.OrdinalIgnoreCase) >= 0) found = t;
                    }
                    if (found != null) return found;
                }
            }
            catch { }
            return Url;
        }

        private static string FindExecutable(string name)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string raw in pathEnv.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    string cand = Path.Combine(dir, name);
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
            return null;
        }

        private static string GetLogTail(string file, int maxLines)
        {
            try
            {
                if (!File.Exists(file)) return "";
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                if (lines.Length == 0) return "";
                int start = Math.Max(0, lines.Length - maxLines);
                return string.Join(Environment.NewLine, lines, start, lines.Length - start);
            }
            catch { return ""; }
        }

        private static void ShowBalloon(string title, string text)
        {
            try { _notify.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        }

        private static void Log(string message)
        {
            AppendLog(TrayLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message);
        }

        private static void AppendLog(string file, string line)
        {
            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
