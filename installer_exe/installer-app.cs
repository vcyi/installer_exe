using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
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
            Application.Run(form);
        }
    }

    class InstallerForm : Form
    {
        const string ConfigJson = @"__CONFIG_JSON__";

        Dictionary<string, object> cfg;
        string productName = "Application";
        string version = "1.0.0";
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
        string systemPathValue = "{app}";
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
        Label statusLabel;

        // Welcome screen
        Panel welcomePanel;
        Label productLabel;
        Label versionLabel;
        Label subtitleLabel;
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
        Label completeLabel;
        Label completeDetail;
        Button finishBtn;

        // Installation
        BackgroundWorker worker;

        // 客户端统一使用浅色主题，避免配置为 light 时仍沿用深色配色。
        static readonly Color BG = ColorTranslator.FromHtml("#f7f8f6");
        static readonly Color BG2 = ColorTranslator.FromHtml("#ffffff");
        static readonly Color Surface = ColorTranslator.FromHtml("#ffffff");
        static readonly Color Cyan = ColorTranslator.FromHtml("#0d9488");
        static readonly Color CyanDim = ColorTranslator.FromHtml("#0f766e");
        static readonly Color Text0 = ColorTranslator.FromHtml("#1c2b3a");
        static readonly Color Text1 = ColorTranslator.FromHtml("#334155");
        static readonly Color Text2 = ColorTranslator.FromHtml("#64748b");
        static readonly Color Border = ColorTranslator.FromHtml("#dae2e8");
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
            ShowWelcome();
        }

        void ParseConfig()
        {
            try
            {
                var js = new JavaScriptSerializer();
                cfg = js.Deserialize<Dictionary<string, object>>(ConfigJson);
                productName = GetStr(cfg, "productName", "Application");
                version = GetStr(cfg, "version", "1.0.0");
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
                systemPathValue = GetStr(cfg, "systemPathValue", GetStr(cfg, "environmentValue", "{app}"));
                selectedPath = installPath;
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
            Size = new Size(760, 500);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = BG;
            DoubleBuffered = true;
            Resize += (s, e) => LayoutResponsiveControls();
        }

        void BuildTitleBar()
        {
            titleBar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = BG2 };
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

            titleLabel = new Label
            {
                Text = productName + " · 安装程序",
                Font = MainFont,
                ForeColor = Text1,
                Location = new Point(20, 12),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            titleBar.Controls.Add(titleLabel);

            closeBtn = new Button
            {
                Text = "×",
                Font = new Font("Microsoft YaHei", 12F),
                ForeColor = Text2,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(36, 30),
                Location = new Point(720, 6),
                Cursor = Cursors.Hand
            };
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#e74c3c");
            closeBtn.Click += (s, e) => Close();
            titleBar.Controls.Add(closeBtn);
            titleBar.Resize += (s, e) => closeBtn.Left = Math.Max(0, titleBar.ClientSize.Width - closeBtn.Width - 6);

            Controls.Add(titleBar);
        }

        void BuildContent()
        {
            content = new Panel { Dock = DockStyle.Fill, BackColor = BG };

            // Status label at bottom
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

        void BuildWelcomePanel()
        {
            welcomePanel = new Panel { Dock = DockStyle.Fill, BackColor = BG };

            var iconPanel = new Panel
            {
                Size = new Size(64, 64),
                Location = new Point(40, 50),
                BackColor = Color.Transparent
            };
            iconPanel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Cyan, CyanDim, LinearGradientMode.Vertical))
                {
                    var path = RoundRect(new Rectangle(0, 0, 64, 64), 14);
                    g.FillPath(brush, path);
                }
                using (var brush = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(productName.Substring(0, 1), new Font("Microsoft YaHei", 24F, FontStyle.Bold), brush, new RectangleF(0, 0, 64, 64), sf);
                }
            };
            welcomePanel.Controls.Add(iconPanel);

            productLabel = new Label
            {
                Text = productName,
                Font = BigFont,
                ForeColor = Text0,
                Location = new Point(120, 45),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            welcomePanel.Controls.Add(productLabel);

            versionLabel = new Label
            {
                Text = "版本 " + version,
                Font = SmallFont,
                ForeColor = Cyan,
                Location = new Point(122, 80),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            welcomePanel.Controls.Add(versionLabel);

            subtitleLabel = new Label
            {
                Text = "选择安装方式以继续",
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

            content.Controls.Add(welcomePanel);
        }

        Button CreateActionButton(string title, string desc, bool primary)
        {
            var btn = new Button
            {
                Size = new Size(680, 80),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = primary ? Color.FromArgb(229, 246, 243) : Surface,
                ForeColor = Text0,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = MainFont,
                Padding = new Padding(20, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = primary ? Cyan : Border;
            btn.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(209, 234, 229) : Color.FromArgb(244, 247, 249);

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
            quickPathPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false, Padding = new Padding(40, 30, 40, 20) };
            var heading = new Label { Text = "选择安装路径", Font = TitleFont, ForeColor = Text0, Dock = DockStyle.Top, Height = 30, BackColor = Color.Transparent };
            var detail = new Label { Text = "快速安装将仅安装基础运行环境。", Font = MainFont, ForeColor = Text2, Dock = DockStyle.Top, Height = 30, BackColor = Color.Transparent };
            var pathLabel = new Label { Text = "安装路径", Font = MainFont, ForeColor = Text1, Dock = DockStyle.Top, Height = 28, BackColor = Color.Transparent };
            var pathRow = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.Transparent };
            quickPathBox = new TextBox { Dock = DockStyle.Fill, Font = MainFont, ForeColor = Text0, BackColor = Surface, BorderStyle = BorderStyle.FixedSingle, Text = selectedPath };
            quickPathBox.TextChanged += (s, e) => selectedPath = quickPathBox.Text;
            quickBrowseBtn = new Button { Text = "浏览", Dock = DockStyle.Right, Width = 90, Font = MainFont, ForeColor = Cyan, BackColor = Surface, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            quickBrowseBtn.FlatAppearance.BorderColor = Border;
            quickBrowseBtn.Click += (s, e) => { string path; if (BrowseFolder("选择安装路径", selectedPath, out path)) { selectedPath = path; quickPathBox.Text = path; } };
            pathRow.Controls.Add(quickPathBox);
            pathRow.Controls.Add(quickBrowseBtn);
            var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
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
            content.Controls.Add(quickPathPanel);
        }

        void BuildCustomPanel()
        {
            customPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };

            var headerPanel = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(40, 20, 40, 0), BackColor = Color.Transparent };
            var heading = new Label
            {
                Text = "自定义安装",
                Font = TitleFont,
                ForeColor = Text0,
                Dock = DockStyle.Top,
                Height = 27,
                AutoEllipsis = false,
                BackColor = Color.Transparent
            };
            var pathLabel = new Label
            {
                Text = "选择安装路径和可选组件",
                Font = MainFont,
                ForeColor = Text2,
                Dock = DockStyle.Top,
                Height = 25,
                AutoEllipsis = false,
                BackColor = Color.Transparent
            };
            headerPanel.Controls.Add(pathLabel);
            headerPanel.Controls.Add(heading);

            var optionsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 8, 40, 10), BackColor = Color.Transparent };
            customPanel.Controls.Add(optionsPanel);
            customPanel.Controls.Add(headerPanel);

            // Path selection
            var installPathLabel = new Label
            {
                Text = "安装路径",
                Font = MainFont,
                ForeColor = Text1,
                Dock = DockStyle.Top,
                Height = 25,
                AutoEllipsis = false,
                BackColor = Color.Transparent
            };
            optionsPanel.Controls.Add(installPathLabel);

            pathBox = new TextBox
            {
                Location = new Point(0, 28),
                Size = new Size(540, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = MainFont,
                ForeColor = Text0,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Text = selectedPath
            };
            pathBox.TextChanged += (s, e) => selectedPath = pathBox.Text;
            optionsPanel.Controls.Add(pathBox);

            browseBtn = new Button
            {
                Text = "浏览",
                Location = new Point(550, 27),
                Size = new Size(90, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
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
            optionsPanel.Controls.Add(browseBtn);

            // Components
            var compLabel = new Label
            {
                Text = "可选组件",
                Font = MainFont,
                ForeColor = Text1,
                Location = new Point(0, 78),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            optionsPanel.Controls.Add(compLabel);

            compPanel = new Panel
            {
                Location = new Point(0, 103),
                Size = new Size(640, 150),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };
            optionsPanel.Controls.Add(compPanel);

            // Bottom actions stay visible at the bottom of the responsive options area.
            var actionsPanel = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
            optionsPanel.Controls.Add(actionsPanel);
            backBtn1 = new Button
            {
                Text = "返回",
                Location = new Point(0, 5),
                Size = new Size(100, 38),
                Font = MainFont,
                ForeColor = Text2,
                BackColor = Surface,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            backBtn1.FlatAppearance.BorderColor = Border;
            backBtn1.Click += (s, e) => ShowWelcome();
            actionsPanel.Controls.Add(backBtn1);

            startInstallBtn = new Button
            {
                Text = "开始安装",
                Location = new Point(520, 5),
                Size = new Size(120, 38),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = MainFont,
                ForeColor = Color.White,
                BackColor = Cyan,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            startInstallBtn.Click += (s, e) => StartCustomInstall();
            actionsPanel.Controls.Add(startInstallBtn);

            content.Controls.Add(customPanel);
        }

        void LayoutResponsiveControls()
        {
            if (content == null) return;
            int width = Math.Max(1, content.ClientSize.Width);
            int side = Math.Min(40, Math.Max(16, width / 14));
            int usable = Math.Max(200, width - side * 2);
            if (welcomePanel != null)
            {
                quickBtn.Width = usable; quickBtn.Left = side;
                customBtn.Width = usable; customBtn.Left = side;
            }
            if (quickPathPanel != null)
            {
                quickPathPanel.Padding = new Padding(side, 30, side, 20);
                quickStartBtn.Left = Math.Max(0, quickPathPanel.ClientSize.Width - side * 2 - quickStartBtn.Width);
            }
            if (customPanel != null)
            {
                var options = pathBox.Parent;
                options.Padding = new Padding(side, 8, side, 10);
                startInstallBtn.Left = Math.Max(0, options.ClientSize.Width - side * 2 - startInstallBtn.Width);
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
                    Text = comp.name + (comp.required ? " (必选)" : ""),
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
            installPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };

            var heading = new Label
            {
                Text = "正在安装",
                Font = TitleFont,
                ForeColor = Text0,
                Location = new Point(40, 30),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            installPanel.Controls.Add(heading);

            installStatusLabel = new Label
            {
                Text = "准备中...",
                Font = MainFont,
                ForeColor = Cyan,
                Location = new Point(40, 75),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            installPanel.Controls.Add(installStatusLabel);

            progressBar = new ProgressBar
            {
                Location = new Point(40, 105),
                Size = new Size(680, 8),
                Style = ProgressBarStyle.Continuous,
                ForeColor = Cyan,
               BackColor = Surface
            };
            installPanel.Controls.Add(progressBar);

            logBox = new TextBox
            {
                Location = new Point(40, 135),
                Size = new Size(680, 300),
                Font = new Font("Consolas", 8.5F),
                ForeColor = Text2,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            installPanel.Controls.Add(logBox);

            content.Controls.Add(installPanel);
        }

        void BuildCompletePanel()
        {
            completePanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };

            completeIconPanel = new Panel
            {
                Size = new Size(72, 72),
                Location = new Point(344, 80),
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
            completePanel.Controls.Add(completeIconPanel);

            completeLabel = new Label
            {
                Text = "安装完成",
                Font = BigFont,
                ForeColor = Text0,
                Location = new Point(0, 170),
                Size = new Size(760, 36),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            completePanel.Controls.Add(completeLabel);

            completeDetail = new Label
            {
                Text = productName + " 已成功安装到您的计算机。",
                Font = MainFont,
                ForeColor = Text2,
                Location = new Point(0, 215),
                Size = new Size(760, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            completePanel.Controls.Add(completeDetail);

            finishBtn = new Button
            {
                Text = "完成",
                Location = new Point(310, 300),
                Size = new Size(140, 42),
                Font = MainFont,
                ForeColor = Color.White,
                BackColor = Cyan,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            finishBtn.Click += (s, e) => Close();
            completePanel.Controls.Add(finishBtn);

            content.Controls.Add(completePanel);
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

        void BeginInstall()
        {
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

        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var w = (BackgroundWorker)sender;
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
                    sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { selectedPath });
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    w.ReportProgress(70, "创建开始菜单快捷方式");
                }
                if (createDesktop)
                {
                    string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), productName + ".lnk");
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exeTarget });
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

                WriteUninstallManifest(exeTarget, systemPathEntry);
                w.ReportProgress(77, "写入卸载清理信息");

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

        void WriteUninstallManifest(string exeTarget, string systemPathEntry)
        {
            string uninstallExe = Path.Combine(selectedPath, productName + "-uninstall.exe");
            File.Copy(Application.ExecutablePath, uninstallExe, true);
            var manifest = new Dictionary<string, object>();
            manifest["productName"] = productName;
            manifest["installPath"] = selectedPath;
            manifest["desktopShortcut"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), productName + ".lnk");
            manifest["startMenuDirectory"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productName);
            manifest["startupEntryName"] = string.IsNullOrEmpty(startupName) ? productName : startupName;
            manifest["systemPathEntry"] = systemPathEntry;
            manifest["cleanupDesktopShortcut"] = cleanupDesktop;
            manifest["cleanupStartMenuShortcut"] = cleanupStartMenu;
            manifest["cleanupStartupEntry"] = cleanupStartup;
            manifest["cleanupInstallDirectory"] = cleanupInstallDir;
            File.WriteAllText(Path.Combine(selectedPath, ".installer-uninstall.json"), new JavaScriptSerializer().Serialize(manifest));
            RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + productName);
            key.SetValue("DisplayName", productName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("UninstallString", "\"" + uninstallExe + "\" --uninstall");
            key.SetValue("InstallLocation", selectedPath);
            key.Close();
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
        static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
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
                string product = Str(cfg, "productName");
                if (MessageBox.Show("确定要卸载 " + product + " 吗？", "卸载确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (Bool(cfg, "cleanupDesktopShortcut")) { string p = Str(cfg, "desktopShortcut"); if (File.Exists(p)) File.Delete(p); }
                if (Bool(cfg, "cleanupStartMenuShortcut")) { string p = Str(cfg, "startMenuDirectory"); if (Directory.Exists(p)) Directory.Delete(p, true); }
                if (Bool(cfg, "cleanupStartupEntry")) Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").DeleteValue(Str(cfg, "startupEntryName"), false);
                string systemPathEntry = Str(cfg, "systemPathEntry");
                if (!string.IsNullOrEmpty(systemPathEntry)) SystemPath.Remove(systemPathEntry);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + product, false);
                bool removeDir = Bool(cfg, "cleanupInstallDirectory");
                MessageBox.Show(removeDir ? "卸载清理已完成，安装目录将在关闭后删除。" : "卸载清理已完成，安装目录已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (removeDir) { Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + installDir + "\"") { CreateNoWindow = true, UseShellExecute = false }); }
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
