using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace InstallerApp
{
    class Program
    {
        [STAThread]
        static void Main()
        {
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
        bool createDesktop = true;
        bool createStartMenu = true;
        bool writeEnv = false;
        string envVar = "";
        string envVal = "";
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

        // Colors
        static readonly Color BG = ColorTranslator.FromHtml("#070b16");
        static readonly Color BG2 = ColorTranslator.FromHtml("#0b1120");
        static readonly Color Surface = ColorTranslator.FromHtml("#0d1426");
        static readonly Color Cyan = ColorTranslator.FromHtml("#00e5ff");
        static readonly Color CyanDim = ColorTranslator.FromHtml("#00b8d4");
        static readonly Color Text0 = ColorTranslator.FromHtml("#f0f4ff");
        static readonly Color Text1 = ColorTranslator.FromHtml("#c4d0e8");
        static readonly Color Text2 = ColorTranslator.FromHtml("#7a8ba8");
        static readonly Color Border = ColorTranslator.FromHtml("#1a2540");
        static readonly Color Emerald = ColorTranslator.FromHtml("#10b981");
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
                createDesktop = GetBool(cfg, "createDesktopShortcut", true);
                createStartMenu = GetBool(cfg, "createStartMenuShortcut", true);
                writeEnv = GetBool(cfg, "writeEnvVars", false);
                envVar = GetStr(cfg, "environmentVariable", "");
                envVal = GetStr(cfg, "environmentValue", "");
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

        void SetupForm()
        {
            Text = productName + " | 安装程序";
            Size = new Size(760, 500);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = BG;
            DoubleBuffered = true;
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
            BuildCustomPanel();
            BuildInstallPanel();
            BuildCompletePanel();

            Controls.Add(content);
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
                using (var brush = new SolidBrush(Color.FromArgb(7, 11, 22)))
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
            quickBtn = CreateActionButton("快速安装", "仅安装基础运行环境，使用默认设置", true);
            quickBtn.Location = new Point(40, 190);
            quickBtn.Click += (s, e) => StartQuickInstall();
            welcomePanel.Controls.Add(quickBtn);

            // Custom install button
            customBtn = CreateActionButton("自定义安装", "选择安装路径和可选组件", false);
            customBtn.Location = new Point(40, 290);
            customBtn.Click += (s, e) => ShowCustom();
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
                BackColor = primary ? Color.FromArgb(0, 229, 255, 20) : Surface,
                ForeColor = Text0,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = MainFont,
                Padding = new Padding(20, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = primary ? Cyan : Border;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 229, 255, 30);

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

        void BuildCustomPanel()
        {
            customPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };

            var heading = new Label
            {
                Text = "自定义安装",
                Font = TitleFont,
                ForeColor = Text0,
                Location = new Point(40, 30),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            customPanel.Controls.Add(heading);

            // Path selection
            var pathLabel = new Label
            {
                Text = "安装路径",
                Font = MainFont,
                ForeColor = Text1,
                Location = new Point(40, 80),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            customPanel.Controls.Add(pathLabel);

            pathBox = new TextBox
            {
                Location = new Point(40, 105),
                Size = new Size(540, 32),
                Font = MainFont,
                ForeColor = Text0,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Text = selectedPath
            };
            pathBox.TextChanged += (s, e) => selectedPath = pathBox.Text;
            customPanel.Controls.Add(pathBox);

            browseBtn = new Button
            {
                Text = "浏览",
                Location = new Point(590, 104),
                Size = new Size(90, 34),
                Font = MainFont,
                ForeColor = Cyan,
                BackColor = Surface,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            browseBtn.FlatAppearance.BorderColor = Border;
            browseBtn.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择安装路径", ShowNewFolderButton = true })
                {
                    if (!string.IsNullOrEmpty(selectedPath))
                        dialog.SelectedPath = selectedPath;
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        selectedPath = dialog.SelectedPath;
                        pathBox.Text = selectedPath;
                    }
                }
            };
            customPanel.Controls.Add(browseBtn);

            // Components
            var compLabel = new Label
            {
                Text = "可选组件",
                Font = MainFont,
                ForeColor = Text1,
                Location = new Point(40, 160),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            customPanel.Controls.Add(compLabel);

            compPanel = new Panel
            {
                Location = new Point(40, 185),
                Size = new Size(640, 150),
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };
            customPanel.Controls.Add(compPanel);

            // Back button
            backBtn1 = new Button
            {
                Text = "返回",
                Location = new Point(40, 400),
                Size = new Size(100, 38),
                Font = MainFont,
                ForeColor = Text2,
                BackColor = Surface,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            backBtn1.FlatAppearance.BorderColor = Border;
            backBtn1.Click += (s, e) => ShowWelcome();
            customPanel.Controls.Add(backBtn1);

            // Start install button
            startInstallBtn = new Button
            {
                Text = "开始安装",
                Location = new Point(560, 400),
                Size = new Size(120, 38),
                Font = MainFont,
                ForeColor = Color.FromArgb(7, 11, 22),
                BackColor = Cyan,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            startInstallBtn.Click += (s, e) => StartCustomInstall();
            customPanel.Controls.Add(startInstallBtn);

            content.Controls.Add(customPanel);
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

            var iconPanel = new Panel
            {
                Size = new Size(72, 72),
                Location = new Point(344, 80),
                BackColor = Color.Transparent
            };
            iconPanel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(16, 185, 129, 40)))
                    g.FillEllipse(brush, new Rectangle(0, 0, 72, 72));
                using (var pen = new Pen(Emerald, 3))
                    g.DrawEllipse(pen, new Rectangle(2, 2, 68, 68));
                // Checkmark
                using (var pen = new Pen(Emerald, 4))
                {
                    g.DrawLine(pen, 22, 38, 32, 48);
                    g.DrawLine(pen, 32, 48, 52, 26);
                }
            };
            completePanel.Controls.Add(iconPanel);

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
                ForeColor = Color.FromArgb(7, 11, 22),
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
            customPanel.Visible = true;
            installPanel.Visible = false;
            completePanel.Visible = false;
            statusLabel.Text = "配置安装选项";
        }

        void StartQuickInstall()
        {
            selectedPath = installPath;
            selectedComps.Clear();
            foreach (var c in components) if (c.required) selectedComps.Add(c.name);
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

                // Shortcuts
                string exeTarget = !string.IsNullOrEmpty(mainExe) ? Path.Combine(selectedPath, mainExe) : selectedPath;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);

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

                // Environment variable
                if (writeEnv && !string.IsNullOrEmpty(envVar))
                {
                    string val = string.IsNullOrEmpty(envVal) ? selectedPath : envVal.Replace("{app}", selectedPath);
                    Environment.SetEnvironmentVariable(envVar, val, EnvironmentVariableTarget.Machine);
                    w.ReportProgress(75, "设置环境变量: " + envVar);
                }

                // Download components
                var toDownload = new List<CompInfo>();
                foreach (var name in selectedComps)
                    foreach (var c in components)
                        if (c.name == name && !string.IsNullOrEmpty(c.downloadUrl))
                            toDownload.Add(c);

                if (toDownload.Count > 0)
                {
                    string dlDir = Path.Combine(selectedPath, "downloads");
                    if (!Directory.Exists(dlDir)) Directory.CreateDirectory(dlDir);
                    int dlBase = 75, dlRange = 20;
                    for (int i = 0; i < toDownload.Count; i++)
                    {
                        var c = toDownload[i];
                        w.ReportProgress(dlBase, "下载: " + c.name);
                        try
                        {
                            string fn = System.Text.RegularExpressions.Regex.Replace(c.name, @"[^\w]", "_") + ".dat";
                            string dest = Path.Combine(dlDir, fn);
                            using (var client = new WebClient())
                                client.DownloadFile(c.downloadUrl, dest);
                            w.ReportProgress(dlBase + (int)((float)(i + 1) / toDownload.Count * dlRange), "已下载: " + c.name);
                        }
                        catch (Exception ex)
                        {
                            w.ReportProgress(dlBase, "下载失败: " + c.name + " - " + ex.Message);
                        }
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
            statusLabel.Text = "安装完成";
            if (e.Error != null)
            {
                completeLabel.Text = "安装出现问题";
                completeLabel.ForeColor = ColorTranslator.FromHtml("#ef4444");
                completeDetail.Text = e.Error.Message;
            }
            else if (e.Result is string)
            {
                completeLabel.Text = "安装出现问题";
                completeLabel.ForeColor = ColorTranslator.FromHtml("#ef4444");
                completeDetail.Text = (string)e.Result;
            }
            else
            {
                completeLabel.Text = "安装完成";
                completeLabel.ForeColor = Text0;
                completeDetail.Text = productName + " 已成功安装到您的计算机。";
            }
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

    class CompInfo
    {
        public string name;
        public string downloadUrl;
        public bool required;
    }

    class Win32
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
    }
}
