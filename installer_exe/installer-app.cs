using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InstallerApp
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length == 4 && string.Equals(args[0], "--cleanup", StringComparison.OrdinalIgnoreCase)) { InstallerMaintenance.RunCleanupHelper(args[1], args[2], args[3]); return; }
            string readyFile = GetReadyFilePath(args);
            bool uninstallRequested = args != null && args.Length > 0 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase);
            bool uninstallCopy = Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith("-uninstall", StringComparison.OrdinalIgnoreCase);
            if (uninstallRequested || uninstallCopy)
            {
                InstallerMaintenance.Uninstall(Application.ExecutablePath);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new InstallerForm();
            bool previewComplete = args != null && args.Any(a => string.Equals(a, "--preview-complete", StringComparison.OrdinalIgnoreCase));
            // 只有主窗体真正显示后才通知准备界面，保证 100% 与安装窗口出现同步。
            form.Shown += (s, e) => { WriteReadyFile(readyFile); if (previewComplete) form.ShowCompletePreview(); };
            Application.Run(form);
        }

        static string GetReadyFilePath(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--ready-file", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        static void WriteReadyFile(string readyFile)
        {
            if (string.IsNullOrEmpty(readyFile)) return;
            try
            {
                string directory = Path.GetDirectoryName(readyFile);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temporaryFile = readyFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryFile, DateTime.UtcNow.ToString("o"));
                File.Move(temporaryFile, readyFile);
            }
            catch
            {
                // 就绪旗标仅用于通知启动器，写入失败不影响安装流程。
            }
        }
    }

    class OwnedFile
    {
        public string relativePath;
        public string sha256;
    }

    class InstallerForm : Form
    {
        const string ConfigJson = @"__CONFIG_JSON__";

        Dictionary<string, object> cfg;
        string productName = "Application";
        string version = "1.0.0";
        string productId = "";
        string detectedUpgradePath = "";
        string installPath = "";
        string mainExe = "";
        bool allowCustomInstall = true;
        bool allowInstallPathSelection = false;
        bool addToSystemPath = false;
        bool createDesktop = true;
        bool createStartMenu = true;
        bool createStartup = false;
        bool cleanupDesktop = true;
        bool cleanupStartMenu = true;
        bool cleanupStartup = true;
        bool cleanupInstallDir = false;
        string startupName = "";
        string startupArgs = "";
        string desktopArgs = "";
        string startMenuArgs = "";
        string systemPathValue = "{app}";
        string controlPanelIcon = "";
        string sourceDir = "";
        List<CompInfo> components = new List<CompInfo>();

        // UI state
        int screen = 0; // 0=welcome, 1=custom, 2=installing, 3=complete
        string selectedPath = "";
        HashSet<string> selectedComps = new HashSet<string>();

        // Controls
        Panel titleBar;
        Label titleLabel;
        Button closeBtn;
        Panel content;
        Panel pageHost;
        Label statusLabel;

        // Welcome screen
        Panel welcomePanel;
        Label productLabel;
        Label versionLabel;
        Label subtitleLabel;
        PictureBox productLogo;
        Button quickBtn;
        Button customBtn;

        // Quick path screen
        Panel quickPathPanel;
        TextBox quickPathBox;
        Button quickBrowseBtn;
        Button quickStartBtn;
        Button quickBackBtn;

        // Custom screen
        Panel customPanel;
        Panel customHeaderPanel;
        Panel customOptionsPanel;
        TextBox pathBox;
        Button browseBtn;
        Panel compPanel;
        Button startInstallBtn;
        Button backBtn1;

        // Install screen
        Panel installPanel;
        ProgressBar progressBar;
        Label installStatusLabel;
        TextBox logBox;

        // Complete screen
        Panel completePanel;
        Panel completeContent;
        Label completeLabel;
        Label completeDetail;
        Button finishBtn;

        // Installation
        BackgroundWorker worker;

        // Apple-inspired light system: frosted white layers, iOS blue accent and quiet contrast.
        static readonly Color BG = ColorTranslator.FromHtml("#f2f4fa");
        static readonly Color BG2 = ColorTranslator.FromHtml("#fafbff");
        static readonly Color Surface = ColorTranslator.FromHtml("#ffffff");
        static readonly Color Cyan = ColorTranslator.FromHtml("#0a84ff");
        static readonly Color CyanDim = ColorTranslator.FromHtml("#006ee6");
        static readonly Color Text0 = ColorTranslator.FromHtml("#1c1c1e");
        static readonly Color Text1 = ColorTranslator.FromHtml("#3a3a3c");
        static readonly Color Text2 = ColorTranslator.FromHtml("#6e6e73");
        static readonly Color Border = ColorTranslator.FromHtml("#dee3ee");
        static readonly Color Emerald = ColorTranslator.FromHtml("#10b981");
        static readonly Color Error = ColorTranslator.FromHtml("#dc2626");
        bool installSucceeded = true;
        Panel completeIconPanel;
        static readonly Font MainFont = new Font("Microsoft YaHei", 9F);
        static readonly Font TitleFont = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
        static readonly Font BigFont = new Font("Microsoft YaHei", 20F, FontStyle.Bold);
        static readonly Font SmallFont = new Font("Microsoft YaHei", 8F);

        public InstallerForm()
        {
            ParseConfig();
            SetupForm();
            BuildTitleBar();
            BuildContent();
            Shown += (s, e) => BeginInvoke((Action)LayoutResponsiveControls);
            ShowWelcome();
        }

        string ProductUninstallRegistryPath() { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + productId; }
        string FindExistingInstallPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProductUninstallRegistryPath()))
                {
                    string path = key == null ? "" : Convert.ToString(key.GetValue("InstallLocation", ""));
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return "";
                    string manifest = Path.Combine(path, ".installer-uninstall.json");
                    if (!File.Exists(manifest)) return "";
                    var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
                    string recordedId = GetStr(record, "productId", "").Trim().Trim('{', '}');
                    string recordedPath = GetStr(record, "installPath", "");
                    if (!string.Equals(recordedId, productId, StringComparison.OrdinalIgnoreCase)) return "";
                    if (!string.Equals(Path.GetFullPath(recordedPath).TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "";
                    return path;
                }
            }
            catch { return ""; }
        }
        bool IsForeignProductDirectory(string path, out string owner)
        {
            owner = "";
            try
            {
                string manifest = Path.Combine(path, ".installer-uninstall.json");
                if (!File.Exists(manifest)) return false;
                var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
                string recordedId = GetStr(record, "productId", "").Trim().Trim('{', '}');
                owner = GetStr(record, "productName", "其他产品");
                return !string.Equals(recordedId, productId, StringComparison.OrdinalIgnoreCase);
            }
            catch { owner = "未知产品"; return true; }
        }
        void ParseConfig()
        {
            try
            {
                var js = new JavaScriptSerializer();
                cfg = js.Deserialize<Dictionary<string, object>>(ConfigJson);
                productName = GetStr(cfg, "productName", "Application");
                version = GetStr(cfg, "version", "1.0.0");
                productId = GetStr(cfg, "productId", GetStr(cfg, "upgradeCode", "")).Trim().Trim('{', '}').ToUpperInvariant();
                Guid parsedProductId; if (!Guid.TryParse(productId, out parsedProductId)) throw new InvalidDataException("产品唯一 ID 无效，已停止安装。");
                productId = parsedProductId.ToString("D").ToUpperInvariant();
                installPath = GetStr(cfg, "installPath", @"C:\Program Files\" + productName);
                mainExe = GetStr(cfg, "mainExe", "");
                allowCustomInstall = GetBool(cfg, "allowCustomInstall", true);
                allowInstallPathSelection = GetBool(cfg, "allowInstallPathSelection", false);
                addToSystemPath = GetBool(cfg, "addToSystemPath", false);
                createDesktop = GetBool(cfg, "createDesktopShortcut", true);
                createStartMenu = GetBool(cfg, "createStartMenuShortcut", true);
                createStartup = GetBool(cfg, "createStartupEntry", false);
                cleanupDesktop = GetBool(cfg, "cleanupDesktopShortcut", true);
                cleanupStartMenu = GetBool(cfg, "cleanupStartMenuShortcut", true);
                cleanupStartup = GetBool(cfg, "cleanupStartupEntry", true);
                cleanupInstallDir = GetBool(cfg, "cleanupInstallDirectory", false);
                startupName = GetStr(cfg, "startupEntryName", productName);
                startupArgs = GetStr(cfg, "startupArguments", "");
                desktopArgs = GetStr(cfg, "desktopArguments", "");
                startMenuArgs = GetStr(cfg, "startMenuArguments", "");
                systemPathValue = GetStr(cfg, "systemPathValue", GetStr(cfg, "environmentValue", "{app}"));
                controlPanelIcon = GetStr(cfg, "controlPanelIcon", "");
                selectedPath = installPath;
                detectedUpgradePath = FindExistingInstallPath();
                if (!string.IsNullOrEmpty(detectedUpgradePath)) { installPath = detectedUpgradePath; selectedPath = detectedUpgradePath; }
                sourceDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "source");

                if (cfg.ContainsKey("optionalComponents"))
                {
                    var arr = cfg["optionalComponents"] as ArrayList;
                    if (arr == null && cfg["optionalComponents"] is Dictionary<string, object>)
                        arr = new ArrayList { cfg["optionalComponents"] };
                    if (arr != null)
                    {
                        foreach (var c in arr)
                        {
                            var d = c as Dictionary<string, object>;
                            if (d == null) continue;
                            components.Add(new CompInfo
                            {
                                name = GetStr(d, "name", ""),
                                downloadUrl = GetStr(d, "downloadUrl", ""),
                                extractPath = GetStr(d, "extractPath", ""),
                                sha256 = GetStr(d, "sha256", ""),
                                sizeBytes = GetLong(d, "sizeBytes", 0),
                                required = GetBool(d, "required", false)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("配置解析失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }
        }

        string GetStr(Dictionary<string, object> d, string key, string def)
        {
            if (d.ContainsKey(key) && d[key] != null) return d[key].ToString();
            return def;
        }

        bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (d.ContainsKey(key) && d[key] is bool) return (bool)d[key];
            return def;
        }

        long GetLong(Dictionary<string, object> d, string key, long def)
        {
            if (!d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToInt64(d[key]); } catch { return def; }
        }

        string FormatSize(long bytes)
        {
            if (bytes <= 0) return "大小未知";
            if (bytes < 1024 * 1024) return Math.Max(1, bytes / 1024) + " KB";
            return (bytes / 1024d / 1024d).ToString(bytes >= 100 * 1024 * 1024 ? "0" : "0.0") + " MB";
        }

        [Flags]
        enum FileOpenOptions : uint { PickFolders = 0x00000020, ForceFileSystem = 0x00000040, PathMustExist = 0x00000800 }

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filters); void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index); void Advise(IntPtr events, out uint cookie); void Unadvise(uint cookie); void SetOptions(FileOpenOptions options); void GetOptions(out FileOpenOptions options); void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder); void GetFolder(out IShellItem folder); void GetCurrentSelection(out IShellItem item); void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name); void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem { void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv); void GetParent(out IShellItem parent); void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string name); }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")] class FileOpenDialog { }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        bool BrowseFolder(string title, string initialDirectory, out string selected)
        {
            selected = null;
            IFileOpenDialog dialog = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialog();
                dialog.SetOptions(FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist);
                dialog.SetTitle(title);
                string initial = !string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer);
                IShellItem folder; Guid iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out folder);
                dialog.SetFolder(folder);
                if (dialog.Show(Handle) != 0) return false;
                IShellItem result; string path;
                dialog.GetResult(out result); result.GetDisplayName(0x80058000, out path);
                selected = path;
                return !string.IsNullOrEmpty(selected);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开文件夹选择窗口: " + ex.Message, "选择安装路径", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally { if (dialog != null) Marshal.ReleaseComObject(dialog); }
        }

        void SetupForm()
        {
            Text = productName + " | 安装程序";
            AutoScaleMode = AutoScaleMode.Dpi;
            // 根据当前工作区限制窗口高度，避免高 DPI 或任务栏环境下窗口超出屏幕而裁切顶部、底部控件。
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            int targetWidth = Math.Min(800, Math.Max(680, workArea.Width - 40));
            int targetHeight = Math.Min(540, Math.Max(440, workArea.Height - 40));
            Size = new Size(targetWidth, targetHeight);
            MinimumSize = new Size(Math.Min(680, workArea.Width), Math.Min(440, workArea.Height));
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = BG;
            DoubleBuffered = true;
            Resize += (s, e) => LayoutResponsiveControls();
        }

        void BuildTitleBar()
        {
            // 使用固定顶部区域而非 Dock，彻底隔离标题栏与内容区，避免控件添加顺序导致叠压。
            titleBar = new Panel { Height = 42, BackColor = BG2, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            titleBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Border, 1))
                    e.Graphics.DrawLine(pen, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1);
            };
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    Win32.ReleaseCapture();
                    Win32.SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

            // 左侧三色圆点仅作视觉元素；右上角提供符合 Windows 习惯的明确关闭按钮。
            Panel redDot = new Panel { BackColor = ColorTranslator.FromHtml("#ff5f57"), Size = new Size(14, 14), Location = new Point(16, 14) };
            Panel minDot = new Panel { BackColor = ColorTranslator.FromHtml("#ffbd2e"), Size = new Size(14, 14), Location = new Point(38, 14) };
            Panel zoomDot = new Panel { BackColor = ColorTranslator.FromHtml("#28c840"), Size = new Size(14, 14), Location = new Point(60, 14) };
            closeBtn = new Button
            {
                Text = "×",
                Font = new Font("Segoe UI", 14F, FontStyle.Regular),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Text2,
                BackColor = Color.Transparent,
                Size = new Size(38, 30),
                Cursor = Cursors.Hand
            };
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#f1f3f7");
            closeBtn.Click += (s, e) => Close();
            titleLabel = new Label
            {
                Text = productName + " · 安装程序",
                Font = MainFont,
                ForeColor = Text1,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(90, 12)
            };
            titleBar.Controls.Add(redDot);
            titleBar.Controls.Add(minDot);
            titleBar.Controls.Add(zoomDot);
            titleBar.Controls.Add(closeBtn);
            titleBar.Controls.Add(titleLabel);
            titleBar.Resize += (s, e) => closeBtn.Location = new Point(Math.Max(0, titleBar.ClientSize.Width - closeBtn.Width - 8), 6);
            titleBar.Location = new Point(0, 0);
            titleBar.Width = ClientSize.Width;
            Controls.Add(titleBar);
        }

        void BuildContent()
        {
            // 内容区从标题栏下方开始，以独立坐标和锚点维护，不依赖 Dock 的控件层级顺序。
            content = new Panel { Location = new Point(0, 42), Size = new Size(ClientSize.Width, Math.Max(0, ClientSize.Height - 42)), BackColor = BG, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };

            // 页面承载区与状态栏分别停靠，状态栏不再覆盖页面底部的操作按钮。
            pageHost = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Text = "",
                Font = SmallFont,
                ForeColor = Text2,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 0, 0),
                BackColor = BG2
            };
            content.Controls.Add(pageHost);
            content.Controls.Add(statusLabel);

            BuildWelcomePanel();
            BuildQuickPathPanel();
            BuildCustomPanel();
            BuildInstallPanel();
            BuildCompletePanel();

            content.Resize += (s, e) => LayoutResponsiveControls();
            Controls.Add(content);
            LayoutResponsiveControls();
        }

        Image LoadPreparationLogo()
        {
            try
            {
                var stream = typeof(InstallerForm).Assembly.GetManifestResourceStream("preparation-logo");
                if (stream == null) return null;
                using (stream) return Image.FromStream(stream);
            }
            catch { return null; }
        }

        void BuildWelcomePanel()
        {
            welcomePanel = new Panel { Dock = DockStyle.Fill, BackColor = BG };

            productLogo = new PictureBox
            {
                Size = new Size(230, 92),
                Location = new Point(40, 36),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Image = LoadPreparationLogo()
            };
            welcomePanel.Controls.Add(productLogo);
            if (productLogo.Image == null)
            {
                productLogo.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new LinearGradientBrush(productLogo.ClientRectangle, Cyan, CyanDim, LinearGradientMode.Vertical)) g.FillPath(brush, RoundRect(productLogo.ClientRectangle, 18));
                    using (var brush = new SolidBrush(Color.White)) { var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; g.DrawString(productName.Substring(0, 1), new Font("Microsoft YaHei", 30F, FontStyle.Bold), brush, productLogo.ClientRectangle, sf); }
                };
            }

            productLabel = new Label
            {
                Text = productName,
                Font = BigFont,
                ForeColor = Text0,
                Location = new Point(142, 48),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            welcomePanel.Controls.Add(productLabel);

            versionLabel = new Label
            {
                Text = "版本 " + version,
                Font = SmallFont,
                ForeColor = Cyan,
                Location = new Point(144, 84),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            welcomePanel.Controls.Add(versionLabel);

            subtitleLabel = new Label
            {
                Text = string.IsNullOrEmpty(detectedUpgradePath) ? "安全、快速、简洁的安装体验" : "检测到已安装版本，将更新至原安装目录", 
                Font = MainFont,
                ForeColor = Text2,
                Location = new Point(40, 140),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            welcomePanel.Controls.Add(subtitleLabel);

            // Quick install button
            quickBtn = CreateActionButton("快速安装", allowInstallPathSelection ? "仅安装基础运行环境，可选择安装路径" : "仅安装基础运行环境，使用默认设置", true);
            quickBtn.Location = new Point(40, 190);
            quickBtn.Click += (s, e) => { if (allowInstallPathSelection) ShowBasePathSelection(); else StartQuickInstall(); };
            welcomePanel.Controls.Add(quickBtn);

            // Custom install button
            customBtn = CreateActionButton("自定义安装", "选择安装路径和可选组件", false);
            customBtn.Location = new Point(40, 290);
            customBtn.Click += (s, e) => ShowCustom();
            customBtn.Visible = allowCustomInstall;
            welcomePanel.Controls.Add(customBtn);

            pageHost.Controls.Add(welcomePanel);
        }

        Button CreateActionButton(string title, string desc, bool primary)
        {
            var btn = new Button
            {
                Size = new Size(680, 80),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = primary ? Color.FromArgb(222, 237, 255) : Surface,
                ForeColor = Text0,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = MainFont,
                Padding = new Padding(20, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = primary ? Cyan : Border;
            btn.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(202, 225, 255) : Color.FromArgb(246, 248, 253);

            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                var rect = new Rectangle(20, 12, 640, 60);
                g.DrawString(title, TitleFont, new SolidBrush(primary ? Cyan : Text0), new PointF(rect.X, rect.Y));
                g.DrawString(desc, SmallFont, new SolidBrush(Text2), new PointF(rect.X, rect.Y + 32));
                // Arrow
                var arrowX = btn.Width - 40;
                var arrowY = btn.Height / 2;
                using (var pen = new Pen(primary ? Cyan : Text2, 2))
                {
                    g.DrawLine(pen, arrowX - 8, arrowY - 6, arrowX, arrowY);
                    g.DrawLine(pen, arrowX, arrowY, arrowX - 8, arrowY + 6);
                }
            };

            return btn;
        }

        void BuildQuickPathPanel()
        {
            quickPathPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };
            // 使用明确的内容坐标，避免多层 Dock 在标题栏下发生叠压并裁切页面标题。
            var heading = new Label { Text = "选择安装路径", Font = TitleFont, ForeColor = Text0, Location = new Point(40, 30), Size = new Size(620, 32), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var detail = new Label { Text = "快速安装将仅安装基础运行环境。", Font = MainFont, ForeColor = Text2, Location = new Point(40, 70), Size = new Size(620, 26), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var pathLabel = new Label { Text = "安装路径", Font = MainFont, ForeColor = Text1, Location = new Point(40, 112), Size = new Size(620, 25), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var pathRow = new Panel { Location = new Point(40, 142), Size = new Size(680, 38), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            quickPathBox = new TextBox { Dock = DockStyle.Fill, Font = MainFont, ForeColor = Text0, BackColor = Surface, BorderStyle = BorderStyle.FixedSingle, Text = selectedPath };
            quickPathBox.TextChanged += (s, e) => selectedPath = quickPathBox.Text;
            quickBrowseBtn = new Button { Text = "浏览", Dock = DockStyle.Right, Width = 90, Font = MainFont, ForeColor = Cyan, BackColor = Surface, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            quickBrowseBtn.FlatAppearance.BorderColor = Border;
            quickBrowseBtn.Click += (s, e) => { string path; if (BrowseFolder("选择安装路径", selectedPath, out path)) { selectedPath = path; quickPathBox.Text = path; } };
            pathRow.Controls.Add(quickPathBox);
            pathRow.Controls.Add(quickBrowseBtn);
            var actions = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(40, 8, 40, 10), BackColor = Color.Transparent };
            quickBackBtn = new Button { Text = "返回", Location = new Point(0, 5), Size = new Size(100, 38), Font = MainFont, ForeColor = Text2, BackColor = Surface, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            quickBackBtn.FlatAppearance.BorderColor = Border;
            quickBackBtn.Click += (s, e) => ShowWelcome();
            quickStartBtn = new Button { Text = "开始安装", Size = new Size(120, 38), Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = MainFont, ForeColor = Color.White, BackColor = Cyan, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            quickStartBtn.Click += (s, e) => StartQuickInstall();
            actions.Controls.Add(quickBackBtn);
            actions.Controls.Add(quickStartBtn);
            quickPathPanel.Controls.Add(actions);
            quickPathPanel.Controls.Add(pathRow);
            quickPathPanel.Controls.Add(pathLabel);
            quickPathPanel.Controls.Add(detail);
            quickPathPanel.Controls.Add(heading);
            pageHost.Controls.Add(quickPathPanel);
        }

        void BuildCustomPanel()
        {
            customPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };

            customHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(40, 20, 40, 0), BackColor = Color.Transparent };
            var heading = new Label
            {
                Text = "自定义安装",
                Font = TitleFont,
                ForeColor = Text0,
                Dock = DockStyle.Top,
                Height = 27,
                BackColor = Color.Transparent
            };
            var subLabel = new Label
            {
                Text = "选择安装路径和可选组件",
                Font = MainFont,
                ForeColor = Text2,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.Transparent
            };
            customHeaderPanel.Controls.Add(subLabel);
            customHeaderPanel.Controls.Add(heading);

            customOptionsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 8, 40, 10), BackColor = Color.Transparent };
            customPanel.Controls.Add(customOptionsPanel);
            customPanel.Controls.Add(customHeaderPanel);

            var installPathLabel = new Label
            {
                Text = "安装路径",
                Font = MainFont,
                ForeColor = Text1,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.Transparent
            };

            var pathRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
            pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Font = MainFont,
                ForeColor = Text0,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Text = selectedPath
            };
            pathBox.TextChanged += (s, e) => selectedPath = pathBox.Text;
            browseBtn = new Button
            {
                Text = "浏览",
                Dock = DockStyle.Right,
                Width = 90,
                Margin = new Padding(0),
                Font = MainFont,
                ForeColor = Cyan,
                BackColor = Surface,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            browseBtn.FlatAppearance.BorderColor = Border;
            browseBtn.Click += (s, e) =>
            {
                string path;
                if (BrowseFolder("选择安装路径", selectedPath, out path))
                {
                    selectedPath = path;
                    pathBox.Text = selectedPath;
                }
            };
            pathRow.Controls.Add(pathBox);
            pathRow.Controls.Add(browseBtn);

            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };

            var compLabel = new Label
            {
                Text = "可选组件",
                Font = MainFont,
                ForeColor = Text1,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.Transparent
            };

            compPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };

            var actionsPanel = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 7, 0, 7), BackColor = Color.Transparent };
            backBtn1 = new Button
            {
                Text = "返回",
                Dock = DockStyle.Left,
                Width = 100,
                Margin = new Padding(0),
                Font = MainFont,
                ForeColor = Text2,
                BackColor = Surface,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            backBtn1.FlatAppearance.BorderColor = Border;
            backBtn1.Click += (s, e) => ShowWelcome();
            startInstallBtn = new Button
            {
                Text = "开始安装",
                Dock = DockStyle.Right,
                Width = 120,
                Margin = new Padding(0),
                Font = MainFont,
                ForeColor = Color.White,
                BackColor = Cyan,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            startInstallBtn.Click += (s, e) => StartCustomInstall();
            actionsPanel.Controls.Add(backBtn1);
            actionsPanel.Controls.Add(startInstallBtn);

            customOptionsPanel.Controls.Add(compPanel);
            customOptionsPanel.Controls.Add(actionsPanel);
            customOptionsPanel.Controls.Add(compLabel);
            customOptionsPanel.Controls.Add(spacer);
            customOptionsPanel.Controls.Add(pathRow);
            customOptionsPanel.Controls.Add(installPathLabel);

            pageHost.Controls.Add(customPanel);
        }

        void LayoutResponsiveControls()
        {
            if (content == null) return;
            int width = Math.Max(1, content.ClientSize.Width);
            int side = Math.Min(40, Math.Max(16, width / 14));
            int usable = Math.Max(200, width - side * 2);
            if (welcomePanel != null)
            {
                // 横向品牌Logo按容器宽度缩放，保持足够高度和清晰可辨的视觉比例。
                int logoWidth = Math.Min(300, Math.Max(190, usable / 3));
                int logoHeight = Math.Min(118, Math.Max(76, logoWidth * 2 / 5));
                productLogo.Size = new Size(logoWidth, logoHeight);
                productLogo.Location = new Point(side, 34);
                productLabel.Location = new Point(side, 34 + logoHeight + 16);
                versionLabel.Location = new Point(side + 2, 34 + logoHeight + 51);
                subtitleLabel.Left = side;
                subtitleLabel.Top = 34 + logoHeight + 92;
                quickBtn.Width = usable; quickBtn.Left = side; quickBtn.Top = subtitleLabel.Bottom + 24;
                customBtn.Width = usable; customBtn.Left = side; customBtn.Top = quickBtn.Bottom + 18;
            }
            if (quickPathPanel != null)
            {
                quickBackBtn.Left = side;
                quickStartBtn.Left = Math.Max(side, quickPathPanel.ClientSize.Width - side - quickStartBtn.Width);
            }
            if (customOptionsPanel != null)
            {
                customHeaderPanel.Padding = new Padding(side, 20, side, 0);
                customOptionsPanel.Padding = new Padding(side, 8, side, 10);
                startInstallBtn.Left = Math.Max(0, customOptionsPanel.ClientSize.Width - side * 2 - startInstallBtn.Width);
            }
            if (installPanel != null)
            {
                installPanel.Padding = new Padding(side, 30, side, 20);
            }
            if (completePanel != null && completeContent != null)
            {
                // 完成页内容容器始终占满可视页面；只居中子控件，杜绝固定宽度容器裁切说明与按钮。
                int cw = completeContent.ClientSize.Width, ch = completeContent.ClientSize.Height;
                int groupHeight = 248;
                int top = Math.Max(12, (ch - groupHeight) / 2);
                completeIconPanel.Location = new Point(Math.Max(0, (cw - completeIconPanel.Width) / 2), top);
                completeLabel.Location = new Point(0, top + 90);
                completeLabel.Size = new Size(cw, 42);
                completeDetail.Location = new Point(0, top + 140);
                completeDetail.Size = new Size(cw, 28);
                finishBtn.Location = new Point(Math.Max(0, (cw - finishBtn.Width) / 2), top + 206);
            }
        }

        void RenderComponents()
        {
            compPanel.Controls.Clear();
            if (components.Count == 0)
            {
                compPanel.Controls.Add(new Label
                {
                    Text = "无可选组件",
                    Font = SmallFont,
                    ForeColor = Text2,
                    Location = new Point(10, 10),
                    AutoSize = true,
                    BackColor = Color.Transparent
                });
                return;
            }
            int y = 8;
            foreach (var comp in components)
            {
                var cb = new CheckBox
                {
                    Text = comp.name + "  ·  " + FormatSize(comp.sizeBytes) + (comp.required ? "  (必选)" : ""),
                    Font = MainFont,
                    ForeColor = comp.required ? Cyan : Text1,
                    Location = new Point(10, y),
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Checked = comp.required,
                    Enabled = !comp.required,
                    Tag = comp.name
                };
                if (comp.required) selectedComps.Add(comp.name);
                compPanel.Controls.Add(cb);
                y += 30;
            }
        }

        void BuildInstallPanel()
        {
            installPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false, Padding = new Padding(40, 30, 40, 20) };

            var heading = new Label
            {
                Text = "正在安装",
                Font = TitleFont,
                ForeColor = Text0,
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.Transparent
            };
            installStatusLabel = new Label
            {
                Text = "准备中...",
                Font = MainFont,
                ForeColor = Cyan,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.Transparent
            };
            progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 8,
                Style = ProgressBarStyle.Continuous,
                ForeColor = Cyan,
                BackColor = Surface
            };
            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                ForeColor = Text2,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };

            installPanel.Controls.Add(logBox);
            installPanel.Controls.Add(spacer);
            installPanel.Controls.Add(progressBar);
            installPanel.Controls.Add(installStatusLabel);
            installPanel.Controls.Add(heading);

            pageHost.Controls.Add(installPanel);
        }

        void BuildCompletePanel()
        {
            completePanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };
            // 固定尺寸内容容器始终在父页正中，避免独立控件位置随页面布局次序偏移。
            completeContent = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            completePanel.Controls.Add(completeContent);
            completePanel.VisibleChanged += (s, e) => { if (completePanel.Visible) BeginInvoke((Action)LayoutResponsiveControls); };
            completePanel.SizeChanged += (s, e) => LayoutResponsiveControls();

            completeIconPanel = new Panel
            {
                Size = new Size(72, 72),
                Location = new Point(224, 0),
                BackColor = Color.Transparent
            };
            completeIconPanel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                Color iconColor = installSucceeded ? Emerald : Error;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(iconColor.R, iconColor.G, iconColor.B, 35)))
                    g.FillEllipse(brush, new Rectangle(0, 0, 72, 72));
                using (var pen = new Pen(iconColor, 3))
                    g.DrawEllipse(pen, new Rectangle(2, 2, 68, 68));
                using (var pen = new Pen(iconColor, 4))
                {
                    if (installSucceeded)
                    {
                        g.DrawLine(pen, 22, 38, 32, 48);
                        g.DrawLine(pen, 32, 48, 52, 26);
                    }
                    else
                    {
                        g.DrawLine(pen, 25, 25, 47, 47);
                        g.DrawLine(pen, 47, 25, 25, 47);
                    }
                }
            };
            completeContent.Controls.Add(completeIconPanel);

            completeLabel = new Label
            {
                Text = "安装完成",
                Font = BigFont,
                ForeColor = Text0,
                Location = new Point(0, 90),
                Size = new Size(520, 42),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            completeContent.Controls.Add(completeLabel);

            completeDetail = new Label
            {
                Text = productName + " 已成功安装到您的计算机。",
                Font = MainFont,
                ForeColor = Text2,
                Location = new Point(0, 140),
                Size = new Size(520, 28),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            completeContent.Controls.Add(completeDetail);

            finishBtn = new Button
            {
                Text = "完成",
                Location = new Point(190, 206),
                Size = new Size(140, 42),
                Font = MainFont,
                ForeColor = Color.White,
                BackColor = Cyan,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            finishBtn.Click += (s, e) => Close();
            completeContent.Controls.Add(finishBtn);

            pageHost.Controls.Add(completePanel);
        }

        void ShowWelcome()
        {
            screen = 0;
            welcomePanel.Visible = true;
            quickPathPanel.Visible = false;
            customPanel.Visible = false;
            installPanel.Visible = false;
            completePanel.Visible = false;
            statusLabel.Text = "请选择安装方式";
        }

        void ShowCustom()
        {
            screen = 1;
            RenderComponents();
            welcomePanel.Visible = false;
            quickPathPanel.Visible = false;
            customPanel.Visible = true;
            installPanel.Visible = false;
            completePanel.Visible = false;
            statusLabel.Text = "配置安装选项";
        }

        void ShowBasePathSelection()
        {
            screen = 1;
            selectedPath = installPath;
            quickPathBox.Text = selectedPath;
            welcomePanel.Visible = false;
            quickPathPanel.Visible = true;
            customPanel.Visible = false;
            installPanel.Visible = false;
            completePanel.Visible = false;
            statusLabel.Text = "选择快速安装路径";
        }

        void StartQuickInstall()
        {
            if (string.IsNullOrEmpty(selectedPath)) selectedPath = installPath;
            selectedComps.Clear();
            BeginInstall();
        }

        void StartCustomInstall()
        {
            selectedComps.Clear();
            foreach (Control c in compPanel.Controls)
            {
                CheckBox cb = c as CheckBox;
                if (cb != null && cb.Checked && cb.Tag is string)
                    selectedComps.Add((string)cb.Tag);
            }
            BeginInstall();
        }

        bool IsSafeInstallDirectory(string path, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path)) { error = "安装目录不能为空。"; return false; }
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) { error = "不能将磁盘根目录设为安装目录。请选择专用的子文件夹。"; return false; }
                if (full.Length < root.Length + 4) { error = "安装目录层级过浅。请选择专用的产品子文件夹。"; return false; }
                return true;
            }
            catch { error = "安装目录无效。"; return false; }
        }

        void BeginInstall()
        {
            string installError;
            if (!IsSafeInstallDirectory(selectedPath, out installError)) { MessageBox.Show(installError, "安装路径不安全", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string foreignOwner;
            if (IsForeignProductDirectory(selectedPath, out foreignOwner)) { MessageBox.Show("该安装目录已属于“" + foreignOwner + "”。为防止不同产品互相覆盖，请选择其他专属目录。", "安装目录冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            screen = 2;
            welcomePanel.Visible = false;
            quickPathPanel.Visible = false;
            customPanel.Visible = false;
            installPanel.Visible = true;
            completePanel.Visible = false;
            statusLabel.Text = "正在安装...";
            progressBar.Value = 0;
            logBox.Clear();

            worker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_Completed;
            worker.RunWorkerAsync();
        }

        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            { byte[] hash = sha.ComputeHash(stream); var text = new System.Text.StringBuilder(hash.Length * 2); for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2")); return text.ToString(); }
        }
        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var w = (BackgroundWorker)sender;
            var installedFiles = new List<OwnedFile>();
            try
            {
                w.ReportProgress(5, "正在安装到: " + selectedPath);

                if (!Directory.Exists(selectedPath))
                    Directory.CreateDirectory(selectedPath);
                w.ReportProgress(10, "创建安装目录");

                // Copy files
                if (Directory.Exists(sourceDir))
                {
                    var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                    int total = files.Length;
                    if (total == 0) total = 1;
                    for (int i = 0; i < files.Length; i++)
                    {
                        string rel = files[i].Substring(sourceDir.Length).TrimStart('\\', '/');
                        string dest = Path.Combine(selectedPath, rel);
                        string dd = Path.GetDirectoryName(dest);
                        if (!Directory.Exists(dd)) Directory.CreateDirectory(dd);
                        File.Copy(files[i], dest, true);
                        installedFiles.Add(new OwnedFile { relativePath = rel.Replace('\\', '/'), sha256 = FileSha256(dest) });
                        int p = 10 + (int)((float)(i + 1) / total * 50);
                        w.ReportProgress(p, "已复制 " + (i + 1) + " / " + files.Length + " 个文件");
                    }
                }
                w.ReportProgress(60, "文件复制完成");

                // Shortcuts: mainExe may be a relative path under the copied source directory.
                string exeTarget = ResolveMainExeTarget();
                if ((createDesktop || createStartMenu || createStartup) && string.IsNullOrEmpty(exeTarget))
                    throw new FileNotFoundException("未找到主程序，无法创建快捷方式。请检查 mainExe 和安装包中的文件。", mainExe);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = (createDesktop || createStartMenu) ? Activator.CreateInstance(shellType) : null;

                if (createStartMenu)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productName);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string lnk = Path.Combine(dir, productName + ".lnk");
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exeTarget });
                    sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { startMenuArgs });
                    sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { selectedPath });
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    w.ReportProgress(70, "创建开始菜单快捷方式");
                }
                if (createDesktop)
                {
                    string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), productName + ".lnk");
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exeTarget });
                    sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { desktopArgs });
                    sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { selectedPath });
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    w.ReportProgress(72, "创建桌面快捷方式");
                }

                // Current-user startup entry
                if (createStartup)
                {
                    string runName = string.IsNullOrEmpty(startupName) ? productName : startupName;
                    string runValue = "\"" + exeTarget + "\"" + (string.IsNullOrEmpty(startupArgs) ? "" : " " + startupArgs);
                    Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").SetValue(runName, runValue, RegistryValueKind.String);
                    w.ReportProgress(74, "创建启动项: " + runName);
                }

                // Only the HKLM system Path is written. {app} resolves to the actual selected install directory.
                string systemPathEntry = "";
                if (addToSystemPath)
                {
                    string resolvedSystemPath = string.IsNullOrEmpty(systemPathValue) ? selectedPath : systemPathValue.Replace("{app}", selectedPath);
                    bool added;
                    SystemPath.Add(resolvedSystemPath, out added);
                    // Only persist an entry we added, so uninstall never removes a pre-existing Path item.
                    if (added) systemPathEntry = resolvedSystemPath;
                    w.ReportProgress(76, added ? "已加入系统 Path: " + systemPathEntry : "系统 Path 已存在相同项，未重复添加");
                }

                WriteUninstallManifest(exeTarget, systemPathEntry, installedFiles);
                w.ReportProgress(77, "写入可验证的卸载文件清单");

                // Download selected external resources to validated paths below the install directory.
                var toDownload = new List<CompInfo>();
                foreach (var name in selectedComps)
                    foreach (var c in components)
                        if (c.name == name && !string.IsNullOrEmpty(c.downloadUrl))
                            toDownload.Add(c);

                if (toDownload.Count > 0)
                {
                    int dlBase = 77, dlRange = 20;
                    for (int i = 0; i < toDownload.Count; i++)
                    {
                        var c = toDownload[i];
                        w.ReportProgress(dlBase, "下载: " + c.name);
                        DownloadResource(c);
                        w.ReportProgress(dlBase + (int)((float)(i + 1) / toDownload.Count * dlRange), "已处理: " + c.name);
                    }
                }

                w.ReportProgress(100, "安装完成！");
            }
            catch (Exception ex)
            {
                e.Result = ex.Message;
                w.ReportProgress(100, "[错误] " + ex.Message);
            }
        }

        string ResolveMainExeTarget()
        {
            if (string.IsNullOrWhiteSpace(mainExe)) return "";
            string relativeExe = mainExe.Trim();
            if (Path.IsPathRooted(relativeExe))
                throw new InvalidOperationException("mainExe 必须是安装目录内的相对路径。");
            string installRoot = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(installRoot, relativeExe));
            if (!target.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return "";
            return target;
        }

        void DownloadResource(CompInfo component)
        {
            // 使用数值常量以保持 .NET Framework 4 编译；TLS 1.1/1.2 枚举名在较新框架才公开。
            const SecurityProtocolType Tls11 = (SecurityProtocolType)768;
            const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls | Tls11 | Tls12;
            string targetDir = SafeInstallSubdirectory(selectedPath, component.extractPath);
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
            string uriFileName;
            try { uriFileName = Path.GetFileName(new Uri(component.downloadUrl).LocalPath); }
            catch { uriFileName = ""; }
            if (string.IsNullOrEmpty(uriFileName)) uriFileName = System.Text.RegularExpressions.Regex.Replace(component.name, @"[^\w.-]", "_") + ".dat";
            string downloadFile = Path.Combine(targetDir, uriFileName);
            try
            {
                using (var client = new WebClient())
                {
                    // 使用 Windows/IE 系统代理设置，并向集成身份验证代理传递当前用户凭据。
                    client.Proxy = WebRequest.DefaultWebProxy;
                    if (client.Proxy != null) client.Proxy.Credentials = CredentialCache.DefaultCredentials;
                    client.DownloadFile(component.downloadUrl, downloadFile);
                }
            }
            catch (WebException ex)
            {
                if (File.Exists(downloadFile)) File.Delete(downloadFile);
                throw new InvalidOperationException(DescribeDownloadError(component.downloadUrl, ex), ex);
            }
            if (!string.IsNullOrEmpty(component.sha256) && !Sha256Matches(downloadFile, component.sha256))
            {
                File.Delete(downloadFile);
                throw new InvalidOperationException("SHA-256 校验失败: " + component.name);
            }
            if (string.Equals(Path.GetExtension(downloadFile), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractZipSafely(downloadFile, targetDir);
                File.Delete(downloadFile);
            }
        }

        static string DescribeDownloadError(string url, WebException ex)
        {
            string detail = "下载失败: " + url + "\r\n状态: " + ex.Status + "\r\n原因: " + ex.Message;
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                detail += "\r\nHTTP 状态: " + (int)response.StatusCode + " " + response.StatusDescription;
                try
                {
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd();
                        if (!string.IsNullOrEmpty(body)) detail += "\r\n服务端响应: " + body.Substring(0, Math.Min(body.Length, 1024));
                    }
                }
                catch { }
            }
            return detail;
        }

        static string SafeInstallSubdirectory(string installDirectory, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return installDirectory;
            if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("资源目标路径必须是相对安装目录的路径。");
            string root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("资源目标路径不能包含 .. 或逃逸安装目录。");
            return target;
        }

        static bool Sha256Matches(string filePath, string expected)
        {
            string normalized = expected.Replace("-", "").Trim();
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                var actual = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) actual.Append(hash[i].ToString("x2"));
                return string.Equals(actual.ToString(), normalized, StringComparison.OrdinalIgnoreCase);
            }
        }

        static void ExtractZipSafely(string zipFile, string targetDirectory)
        {
            string root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipFile))
            {
                foreach (var entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ZIP 包含越界路径: " + entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name)) { if (!Directory.Exists(destination)) Directory.CreateDirectory(destination); continue; }
                    string directory = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    entry.ExtractToFile(destination, true);
                }
            }
        }

        string ProductRegistryKey()
        {
            Guid parsed; if (!Guid.TryParse(productId, out parsed)) throw new InvalidOperationException("产品唯一 ID 无效，无法安全写入卸载信息。");
            return parsed.ToString("D").ToUpperInvariant();
        }
        void WriteUninstallManifest(string exeTarget, string systemPathEntry, List<OwnedFile> installedFiles)
        {
            // Manifest记录每一个可验证的产品文件，卸载时只清理哈希仍匹配的文件，绝不按目录递归删除。
            string uninstallExe = Path.Combine(selectedPath, productName + "-uninstall.exe");
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productName);
            string startupKey = string.IsNullOrEmpty(startupName) ? productName : startupName;
            File.Copy(Application.ExecutablePath, uninstallExe, true);
            installedFiles.Add(new OwnedFile { relativePath = Path.GetFileName(uninstallExe), sha256 = FileSha256(uninstallExe) });
            var manifest = new Dictionary<string, object>();
            manifest["schemaVersion"] = 3;
            manifest["productId"] = ProductRegistryKey();
            manifest["productName"] = productName;
            manifest["installPath"] = selectedPath;
            // 卸载器、清单本身和可选的产品图标也属于本安装器拥有的文件。
            string iconFile = "";
            if (!string.IsNullOrWhiteSpace(controlPanelIcon) && !Path.IsPathRooted(controlPanelIcon)) { string candidate = Path.Combine(selectedPath, controlPanelIcon); if (File.Exists(candidate)) iconFile = candidate; }
            manifest["uninstallExe"] = uninstallExe;
            manifest["mainExeTarget"] = exeTarget;
            manifest["controlPanelIcon"] = iconFile;
            manifest["ownedFiles"] = installedFiles;
            manifest["desktopShortcut"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), productName + ".lnk");
            manifest["desktopArguments"] = desktopArgs;
            manifest["startMenuShortcut"] = Path.Combine(startMenuDir, productName + ".lnk");
            manifest["startMenuArguments"] = startMenuArgs;
            manifest["startMenuDirectory"] = startMenuDir;
            manifest["startupEntryName"] = startupKey;
            manifest["startupEntryValue"] = "\"" + exeTarget + "\"" + (string.IsNullOrEmpty(startupArgs) ? "" : " " + startupArgs);
            manifest["systemPathEntry"] = systemPathEntry;
            manifest["cleanupDesktopShortcut"] = cleanupDesktop;
            manifest["cleanupStartMenuShortcut"] = cleanupStartMenu;
            manifest["cleanupStartupEntry"] = cleanupStartup;
            manifest["cleanupInstallDirectory"] = false;
            File.WriteAllText(Path.Combine(selectedPath, ".installer-uninstall.json"), new JavaScriptSerializer().Serialize(manifest));
            RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductRegistryKey());
            key.SetValue("DisplayName", productName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("UninstallString", "\"" + uninstallExe + "\" --uninstall");
            key.SetValue("InstallLocation", selectedPath);
            if (!string.IsNullOrEmpty(iconFile)) key.SetValue("DisplayIcon", "\"" + iconFile + "\",0");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.Close();
        }

        public void ShowCompletePreview()
        {
            installSucceeded = true;
            welcomePanel.Visible = false; quickPathPanel.Visible = false; customPanel.Visible = false; installPanel.Visible = false;
            completePanel.Visible = true;
            completeLabel.Text = "安装完成";
            completeLabel.ForeColor = Text0;
            completeDetail.Text = productName + " 已成功安装到您的计算机。";
            completePanel.PerformLayout();
            LayoutResponsiveControls();
            completeIconPanel.Invalidate();
        }
        void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar.Value = Math.Min(100, e.ProgressPercentage);
            string msg = e.UserState as string;
            if (msg != null)
            {
                installStatusLabel.Text = msg;
                if (logBox.TextLength > 0) logBox.AppendText("\r\n");
                logBox.AppendText(msg);
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }
        }

        void Worker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            screen = 3;
            installPanel.Visible = false;
            completePanel.Visible = true;
            installSucceeded = e.Error == null && !(e.Result is string);
            statusLabel.Text = installSucceeded ? "安装完成" : "安装失败";
            if (!installSucceeded)
            {
                completeLabel.Text = "安装出现问题";
                completeLabel.ForeColor = Error;
                completeDetail.Text = e.Error != null ? e.Error.Message : (string)e.Result;
            }
            else
            {
                completeLabel.Text = "安装完成";
                completeLabel.ForeColor = Text0;
                completeDetail.Text = productName + " 已成功安装到您的计算机。";
            }
            completeIconPanel.Invalidate();
            BeginInvoke((Action)LayoutResponsiveControls);
        }

        GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Border, 1))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    class InstallerMaintenance
    {
        static string Str(Dictionary<string, object> d, string key) { return d.ContainsKey(key) && d[key] != null ? d[key].ToString() : ""; }
        static bool Bool(Dictionary<string, object> d, string key) { return d.ContainsKey(key) && d[key] is bool && (bool)d[key]; }
        static bool IsDirectChild(string path, string directory, string extension)
        {
            try { return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(directory) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static bool IsOwnedShortcut(string path, string expectedTarget, string expectedArguments)
        {
            if (!File.Exists(path)) return false;
            try { Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType); object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path }); string target = Convert.ToString(sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null)); string args = Convert.ToString(sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null)); return string.Equals(Path.GetFullPath(target), Path.GetFullPath(expectedTarget), StringComparison.OrdinalIgnoreCase) && string.Equals(args ?? "", expectedArguments ?? "", StringComparison.Ordinal); } catch { return false; }
        }
        static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        static bool IsSafeOwnedPath(string installPath, string relativePath, out string fullPath)
        {
            fullPath = "";
            try { if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false; string root=Path.GetFullPath(installPath).TrimEnd('\\')+"\\"; fullPath=Path.GetFullPath(Path.Combine(root,relativePath)); return fullPath.StartsWith(root,StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static void DeleteOwnedFiles(Dictionary<string, object> cfg, string installPath, string manifestPath)
        {
            ArrayList files = cfg.ContainsKey("ownedFiles") ? cfg["ownedFiles"] as ArrayList : null;
            if (files != null) foreach (object item in files)
            {
                var f=item as Dictionary<string, object>; if(f==null) continue; string path;
                if(!IsSafeOwnedPath(installPath,Str(f,"relativePath"),out path) || !File.Exists(path)) continue;
                string expected=Str(f,"sha256"); if(string.IsNullOrWhiteSpace(expected)) continue;
                try { if(string.Equals(InstallerForm.FileSha256(path),expected,StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFullPath(path),Path.GetFullPath(Application.ExecutablePath),StringComparison.OrdinalIgnoreCase)) File.Delete(path); } catch { }
            }
            // 清单本身位于安装根目录且仅在已完成逐项文件校验后删除。
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
            // 仅移除自底向上已经为空的目录；不递归，不删除任何仍含文件的目录。
            try { foreach(string dir in Directory.GetDirectories(installPath,"*",SearchOption.AllDirectories).OrderByDescending(x=>x.Length)) { try { if(Directory.GetFileSystemEntries(dir).Length==0) Directory.Delete(dir,false); } catch { } } } catch { }
        }
        static void StartVerifiedCleanupHelper(string uninstallExe, string installPath)
        {
            // 由临时目录中的独立副本收尾，避免卸载程序自删触发“程序兼容性助手”。
            try
            {
                string helper = Path.Combine(Path.GetTempPath(), "installer-cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Application.ExecutablePath, helper, true);
                Process.Start(new ProcessStartInfo(helper, "--cleanup \"" + uninstallExe + "\" \"" + installPath + "\" " + Process.GetCurrentProcess().Id) { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }
        public static void RunCleanupHelper(string uninstallExe, string installPath, string pidText)
        {
            try
            {
                int pid; if (!int.TryParse(pidText, out pid)) return;
                try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }
                string fullRoot = Path.GetFullPath(installPath).TrimEnd('\\') + "\\";
                string fullUninstall = Path.GetFullPath(uninstallExe);
                if (!fullUninstall.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                try { if (File.Exists(fullUninstall)) File.Delete(fullUninstall); } catch { }
                try { if (Directory.Exists(installPath) && Directory.GetFileSystemEntries(installPath).Length == 0) Directory.Delete(installPath, false); } catch { }
            }
            catch { }
        }
        public static void Uninstall(string uninstallExe)
        {
            string installDir = Path.GetDirectoryName(uninstallExe);
            string manifestPath = Path.Combine(installDir, ".installer-uninstall.json");
            try
            {
                if (!File.Exists(manifestPath)) throw new FileNotFoundException("未找到卸载配置。", manifestPath);
                if (!IsAdministrator())
                {
                    DialogResult result = MessageBox.Show("卸载需要管理员权限以删除系统 Path。是否以管理员身份继续？", "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (result != DialogResult.Yes) return;
                    Process.Start(new ProcessStartInfo(uninstallExe, "--uninstall") { Verb = "runas", UseShellExecute = true });
                    return;
                }
                var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
                string product = Str(cfg, "productName"), installPath = Str(cfg, "installPath"), productId = Str(cfg, "productId"), mainTarget = Str(cfg, "mainExeTarget");
                Guid parsed; if (!Guid.TryParse(productId, out parsed)) throw new InvalidDataException("卸载配置缺少有效的产品唯一 ID，已拒绝执行清理。");
                if (!string.Equals(Path.GetFullPath(installDir).TrimEnd('\\'), Path.GetFullPath(installPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("卸载程序路径与安装记录不一致，已拒绝执行清理。");
                if (MessageBox.Show("确定要卸载 " + product + " 吗？\r\n\r\n将仅删除安装清单中哈希仍匹配的产品文件，并撤销经归属校验的快捷方式、启动项、Path 条目和本产品卸载记录。\r\n任何被修改的文件、未知文件及非空目录都将保留。", "卸载确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs), startMenuDir = Path.Combine(programsDir, product);
                if (Bool(cfg, "cleanupDesktopShortcut")) { string p = Str(cfg, "desktopShortcut"); if (IsDirectChild(p, desktopDir, ".lnk") && IsOwnedShortcut(p, mainTarget, Str(cfg,"desktopArguments"))) File.Delete(p); }
                if (Bool(cfg, "cleanupStartMenuShortcut")) { string p = Str(cfg, "startMenuShortcut"); if (IsDirectChild(p, startMenuDir, ".lnk") && IsOwnedShortcut(p, mainTarget, Str(cfg,"startMenuArguments"))) File.Delete(p); }
                if (Bool(cfg, "cleanupStartupEntry")) { string n=Str(cfg,"startupEntryName"), expected=Str(cfg,"startupEntryValue"); using(RegistryKey run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)) { if(run!=null && string.Equals(Convert.ToString(run.GetValue(n,"")),expected,StringComparison.Ordinal)) run.DeleteValue(n,false); } }
                string systemPathEntry = Str(cfg, "systemPathEntry"); if (!string.IsNullOrEmpty(systemPathEntry) && Path.GetFullPath(systemPathEntry).StartsWith(Path.GetFullPath(installPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) SystemPath.Remove(systemPathEntry);
                DeleteOwnedFiles(cfg, installPath, manifestPath);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + parsed.ToString("D").ToUpperInvariant(), false);
                MessageBox.Show("卸载完成。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // 用户关闭提示后由临时清理助手处理卸载器自身与已空目录。
                StartVerifiedCleanupHelper(uninstallExe, installPath);
            }
            catch (Exception ex) { MessageBox.Show("卸载失败: " + ex.Message, "卸载错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    class CompInfo
    {
        public string name;
        public string downloadUrl;
        public string extractPath;
        public string sha256;
        public long sizeBytes;
        public bool required;
    }

    static class SystemPath
    {
        const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        // Returns whether the value was newly written through the out flag; false means it already existed.
        public static bool Add(string value, out bool added)
        {
            added = Update(value, true);
            return added;
        }
        public static bool Remove(string value) { return Update(value, false); }
        static bool Update(string value, bool add)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            bool changed = false;
            bool found = false;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, true))
            {
                if (key == null) throw new InvalidOperationException("无法打开系统环境变量注册表项，需要管理员权限。");
                string current = Convert.ToString(key.GetValue("Path", ""));
                string[] parts = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                var values = new List<string>();
                foreach (string part in parts)
                {
                    string item = part.Trim();
                    if (string.IsNullOrEmpty(item)) continue;
                    if (string.Equals(item, value.Trim(), StringComparison.OrdinalIgnoreCase)) { found = true; if (!add) { changed = true; continue; } }
                    bool duplicate = false; foreach (string existing in values) if (string.Equals(existing, item, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                    if (!duplicate) values.Add(item); else changed = true;
                }
                if (add && !found) { values.Add(value.Trim()); changed = true; }
                if (changed) key.SetValue("Path", string.Join(";", values.ToArray()), RegistryValueKind.ExpandString);
            }
            if (changed) EnvironmentNotifier.Broadcast();
            return add ? !found : changed;
        }
    }

    static class EnvironmentNotifier
    {
        const int HWND_BROADCAST = 0xffff;
        const int WM_SETTINGCHANGE = 0x001a;
        const int SMTO_ABORTIFHUNG = 0x0002;
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
        public static void Broadcast()
        {
            IntPtr ignored;
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 5000, out ignored);
        }
    }

    class Win32
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
    }
}
