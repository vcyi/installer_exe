using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

public class InstallerStudioNative : Form
{
    readonly string scriptDir = AppDomain.CurrentDomain.BaseDirectory;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    // 浅色教育科技主题：暖白画布、洁净卡片与蓝绿色操作强调。
    readonly Color Canvas = Color.FromArgb(247, 248, 246), Surface = Color.FromArgb(255, 255, 255), Card = Color.FromArgb(255, 255, 255);
    readonly Color Field = Color.FromArgb(244, 247, 249), Line = Color.FromArgb(218, 226, 232), Cyan = Color.FromArgb(13, 148, 136), TextMain = Color.FromArgb(28, 43, 58), TextMuted = Color.FromArgb(100, 116, 139);
    TextBox productName, version, publisher, subtitle, sourceDir, outputDir, installPath, mainExe, iconPath, systemPathValue, startupName, startupArgs, scanResult;
    CheckBox customInstall, allowInstallPathSelection, addToSystemPath, desktop, startMenu, startup, cleanDesktop, cleanStartMenu, cleanStartup, cleanInstallDir;
    ComboBox theme;
    DataGridView resources;
    RichTextBox logBox;
    ProgressBar progress;
    Label buildState, outputLabel, headerPage, headerHint;
    Timer statusTimer;
    Timer saveTimer;
    bool loadingConfig;
    TabControl pageTabs;
    Button[] pageButtons;

    public InstallerStudioNative()
    {
        Text = "Installer Studio Native | 教育部署控制台";
        // 支持在 1024 x 660 左右的窗口完整显示；内容区域会按页垂直滚动。
        StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(980, 620); Size = new Size(1240, 800);
        Font = new Font("Microsoft YaHei UI", 9F); BackColor = Canvas; ForeColor = TextMain;
        BuildUi();
        statusTimer = new Timer(); statusTimer.Interval = 700; statusTimer.Tick += delegate { PollStatus(); };
        LoadConfig(Path.Combine(scriptDir, "build-config.json"), false);
        BindAutoSave();
        FormClosing += delegate { SaveDefaultConfig(); };
    }

    class CardPanel : Panel
    {
        public Color BorderColor = Color.FromArgb(39, 73, 101);
        public CardPanel() { DoubleBuffered = true; Padding = new Padding(20); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen p = new Pen(BorderColor)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    }
    Label LabelText(string value, float size, Color color) { return new Label { Text = value, AutoSize = true, ForeColor = color, Font = new Font("Microsoft YaHei UI", size, FontStyle.Regular), BackColor = Color.Transparent }; }
    TextBox TextField(string value) { return new TextBox { Dock = DockStyle.Fill, Text = value ?? "", BackColor = Field, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle, Height = 30 }; }
    Button ActionButton(string text, bool primary)
    {
        Button b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Height = 32, AutoSize = true, Padding = new Padding(12, 0, 12, 0), BackColor = primary ? Cyan : Surface, ForeColor = primary ? Color.White : TextMain, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? Cyan : Line; b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(15, 118, 110) : Color.FromArgb(238, 246, 247); return b;
    }
    void AddRow(TableLayoutPanel p, int row, string label, Control control)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label l = LabelText(label, 9F, TextMuted); l.Anchor = AnchorStyles.Left; l.Margin = new Padding(0, 9, 14, 9);
        control.Margin = new Padding(0, 5, 0, 5); control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        p.Controls.Add(l, 0, row); p.Controls.Add(control, 1, row);
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    string ExistingDirectory(string path) { return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : scriptDir; }
    string InitialDirectoryForFile(string path) { string directory = string.IsNullOrEmpty(path) ? "" : Path.GetDirectoryName(path); return ExistingDirectory(directory); }
    bool BrowseFolder(string title, string initialDirectory, out string selectedPath)
    {
        selectedPath = null; IFileOpenDialog dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialog(); dialog.SetOptions(FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist); dialog.SetTitle(title);
            IShellItem folder; Guid iid = typeof(IShellItem).GUID; string initial = ExistingDirectory(initialDirectory); SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out folder); dialog.SetFolder(folder);
            if (dialog.Show(Handle) != 0) return false;
            IShellItem result; dialog.GetResult(out result); string path; result.GetDisplayName(0x80058000, out path); selectedPath = path; return true;
        }
        catch { return false; }
        finally { if (dialog != null) Marshal.ReleaseComObject(dialog); }
    }
    bool BrowseFile(string title, string initialDirectory, string filter, out string selectedPath)
    {
        selectedPath = null; using (OpenFileDialog dialog = new OpenFileDialog()) { dialog.Title = title; dialog.InitialDirectory = ExistingDirectory(initialDirectory); dialog.Filter = filter; dialog.RestoreDirectory = true; if (dialog.ShowDialog(this) != DialogResult.OK) return false; selectedPath = dialog.FileName; return true; }
    }
    bool SaveFile(string title, string initialDirectory, string filter, string fileName, out string selectedPath)
    {
        selectedPath = null; using (SaveFileDialog dialog = new SaveFileDialog()) { dialog.Title = title; dialog.InitialDirectory = ExistingDirectory(initialDirectory); dialog.Filter = filter; dialog.FileName = fileName; dialog.RestoreDirectory = true; if (dialog.ShowDialog(this) != DialogResult.OK) return false; selectedPath = dialog.FileName; return true; }
    }
    Control BrowseField(TextBox target, bool folder, string filter)
    {
        Panel p = new Panel { Dock = DockStyle.Fill, Height = 30, BackColor = Color.Transparent }; target.Dock = DockStyle.Fill;
        Button b = ActionButton("浏览", false); b.Dock = DockStyle.Right; b.Width = 72; b.AutoSize = false;
        b.Click += delegate { string selected; bool accepted = folder ? BrowseFolder("选择目录", target.Text, out selected) : BrowseFile("选择文件", InitialDirectoryForFile(target.Text), filter, out selected); if (accepted) target.Text = selected; };
        p.Controls.Add(target); p.Controls.Add(b); return p;
    }
    TabPage NewPage(string text) { return new TabPage(text) { Padding = new Padding(22), AutoScroll = true, BackColor = Canvas, ForeColor = TextMain }; }
    void SelectPage(int index)
    {
        if (pageTabs == null || index < 0 || index >= pageTabs.TabPages.Count) return;
        pageTabs.SelectedIndex = index; headerPage.Text = pageTabs.TabPages[index].Text;
        for (int i = 0; pageButtons != null && i < pageButtons.Length; i++) { bool selected = i == index; pageButtons[i].BackColor = selected ? Color.FromArgb(229, 246, 243) : Surface; pageButtons[i].ForeColor = selected ? Cyan : TextMuted; pageButtons[i].FlatAppearance.BorderColor = selected ? Color.FromArgb(166, 216, 209) : Surface; }
    }
    void BuildUi()
    {
        SuspendLayout();
        Panel shell = new Panel { Dock = DockStyle.Fill, BackColor = Canvas }; Controls.Add(shell);
        Panel side = new Panel { Dock = DockStyle.Left, Width = 218, BackColor = Surface, Padding = new Padding(16, 20, 16, 18) };
        side.Paint += delegate(object sender, PaintEventArgs e) { using (Pen p = new Pen(Line)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
        Panel main = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        // WinForms 按 Z 顺序反向处理停靠控件：先加入 Fill，再加入 Left，避免侧栏覆盖主内容左边缘。
        shell.Controls.Add(main); shell.Controls.Add(side);
        BuildSidebar(side); BuildHeader(main); BuildPages(main); ResumeLayout(false);
    }
    void BuildSidebar(Panel side)
    {
        Label mark = LabelText("IS", 20F, Cyan); mark.Font = new Font("Segoe UI", 20F, FontStyle.Bold); side.Controls.Add(mark);
        Label brand = LabelText("INSTALLER\nSTUDIO", 12F, TextMain); brand.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold); brand.Location = new Point(48, 22); side.Controls.Add(brand);
        Label edition = LabelText("EDTECH DEPLOYMENT CONSOLE", 7.5F, TextMuted); edition.Location = new Point(18, 82); side.Controls.Add(edition);
        Label nav = LabelText("工作区", 8.5F, TextMuted); nav.Location = new Point(18, 132); side.Controls.Add(nav);
        string[] names = { "01  产品与目录", "02  安装行为", "03  外部资源", "04  构建日志" }; pageButtons = new Button[names.Length];
        for (int i = 0; i < names.Length; i++) { int pageIndex = i; Button b = new Button { Text = names[i], TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 1 }, Width = 186, Height = 42, Location = new Point(16, 158 + i * 48), BackColor = Surface, ForeColor = TextMuted, Cursor = Cursors.Hand }; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 248, 247); b.Click += delegate { SelectPage(pageIndex); }; pageButtons[i] = b; side.Controls.Add(b); }
        Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 105, BackColor = Surface }; side.Controls.Add(bottom);
        Label secure = LabelText("本地构建环境", 9F, TextMain); secure.Location = new Point(2, 12); bottom.Controls.Add(secure);
        Label note = LabelText("配置仅保存于本机\n.NET Framework 4 Compatible", 8F, TextMuted); note.Location = new Point(2, 38); bottom.Controls.Add(note);
    }
    void BuildHeader(Panel main)
    {
        Panel head = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Surface, Padding = new Padding(24, 16, 24, 12) }; head.Paint += delegate(object sender, PaintEventArgs e) { using (Pen p = new Pen(Line)) e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1); }; main.Controls.Add(head);
        headerPage = LabelText("产品与目录", 18F, TextMain); headerPage.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold); headerPage.Location = new Point(24, 16); head.Controls.Add(headerPage);
        headerHint = LabelText("配置你的学习产品安装体验与分发资源", 9F, TextMuted); headerHint.Location = new Point(26, 52); head.Controls.Add(headerHint);
        Button build = ActionButton("开始构建", true); build.Dock = DockStyle.Right; build.Width = 116; build.Click += delegate { StartBuild(); }; head.Controls.Add(build);
        Button scan = ActionButton("扫描目录", false); scan.Dock = DockStyle.Right; scan.Width = 96; scan.Margin = new Padding(0, 0, 10, 0); scan.Click += delegate { ScanDirectory(); }; head.Controls.Add(scan);
        MenuStrip menu = new MenuStrip { Dock = DockStyle.Bottom, BackColor = Surface, ForeColor = TextMuted, Renderer = new DarkMenuRenderer(Surface, Card, TextMain) };
        ToolStripMenuItem file = new ToolStripMenuItem("配置文件"); file.DropDownItems.Add("导入 build-config.json...", null, delegate { ImportConfig(); }); file.DropDownItems.Add("导出 build-config.json...", null, delegate { ExportConfig(); }); file.DropDownItems.Add("保存到默认配置", null, delegate { SaveConfig(Path.Combine(scriptDir, "build-config.json")); }); menu.Items.Add(file); head.Controls.Add(menu); MainMenuStrip = menu;
    }
    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        Color back, hover, fore; public DarkMenuRenderer(Color b, Color h, Color f) { back = b; hover = h; fore = f; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(back); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { e.Item.BackColor = e.Item.Selected ? hover : back; base.OnRenderMenuItemBackground(e); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = fore; base.OnRenderItemText(e); }
    }
    void BuildPages(Panel main)
    {
        pageTabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(1, 1), SizeMode = TabSizeMode.Fixed, Multiline = true };
        pageTabs.SelectedIndexChanged += delegate { if (pageTabs.SelectedIndex >= 0) SelectPage(pageTabs.SelectedIndex); };
        main.Controls.Add(pageTabs);
        // Dock 按反向 Z 顺序布局：让标题栏先占据顶部，TabControl 再填充余下空间。
        main.Controls.SetChildIndex(pageTabs, 0);
        BuildProductPage(); BuildBehaviorPage(); BuildResourcesPage(); BuildLogPage(); SelectPage(0);
    }
    CardPanel PageCard(TabPage page, string title, string caption)
    {
        // TabPage 的 AutoScroll 负责纵向溢出；卡片采用显式内容流，避免 Dock + AutoSize 造成标题和表单重叠。
        CardPanel card = new CardPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Card, Margin = new Padding(0, 0, 0, 16), Padding = new Padding(20) };
        FlowLayoutPanel content = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, BackColor = Card, Margin = new Padding(0), Padding = new Padding(0) };
        Label head = LabelText(title, 12F, TextMain); head.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold); head.Margin = new Padding(0, 0, 0, 5);
        Label cap = LabelText(caption, 8.5F, TextMuted); cap.Margin = new Padding(0, 0, 0, 12);
        content.Controls.Add(head); content.Controls.Add(cap); card.Controls.Add(content); page.Controls.Add(card);
        page.SizeChanged += delegate { content.Width = Math.Max(100, page.ClientSize.Width - card.Padding.Horizontal); };
        content.Width = Math.Max(100, page.ClientSize.Width - card.Padding.Horizontal);
        return card;
    }
    void AddCardContent(CardPanel card, Control content)
    {
        FlowLayoutPanel flow = card.Controls[0] as FlowLayoutPanel;
        if (flow == null) { card.Controls.Add(content); return; }
        content.Dock = DockStyle.None; content.Margin = new Padding(0); content.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        flow.Controls.Add(content);
        flow.SizeChanged += delegate { content.Width = Math.Max(100, flow.ClientSize.Width); };
    }
    void BuildProductPage()
    {
        TabPage product = NewPage("产品与目录"); pageTabs.TabPages.Add(product); CardPanel card = PageCard(product, "产品身份与文件路径", "定义学习产品的发布信息、安装位置和输出目标");
        TableLayoutPanel p = FormTable(); AddCardContent(card, p);
        productName = TextField("My Application"); version = TextField("1.0.0"); publisher = TextField(""); subtitle = TextField("安装程序"); sourceDir = TextField(""); outputDir = TextField(scriptDir); installPath = TextField("C:\\Program Files\\My Application"); mainExe = TextField(""); iconPath = TextField(""); scanResult = TextField("尚未扫描"); scanResult.ReadOnly = true;
        AddRow(p, 0, "产品名称 *", productName); AddRow(p, 1, "版本", version); AddRow(p, 2, "发布者", publisher); AddRow(p, 3, "副标题", subtitle); AddRow(p, 4, "基础程序目录 *", BrowseField(sourceDir, true, "")); AddRow(p, 5, "输出目录", BrowseField(outputDir, true, "")); AddRow(p, 6, "默认安装目录", installPath); AddRow(p, 7, "主程序 EXE", mainExe); AddRow(p, 8, "图标文件", BrowseField(iconPath, false, "图标文件 (*.ico)|*.ico|所有文件 (*.*)|*.*")); AddRow(p, 9, "目录扫描", scanResult);
    }
    void BuildBehaviorPage()
    {
        TabPage behavior = NewPage("安装行为"); pageTabs.TabPages.Add(behavior); CardPanel card = PageCard(behavior, "学习终端部署策略", "控制快捷入口、启动任务、系统 Path 和卸载清理规则"); TableLayoutPanel bp = FormTable(); AddCardContent(card, bp);
        customInstall = Check("允许用户自定义安装（路径与可选组件）", true); allowInstallPathSelection = Check("允许基础/快速安装用户选择路径", false); addToSystemPath = Check("将指定路径加入系统 Path（HKLM，需要管理员权限）", false); systemPathValue = TextField("{app}"); desktop = Check("创建桌面快捷方式", true); startMenu = Check("创建开始菜单快捷方式", true); startup = Check("创建当前用户启动项", false); startupName = TextField(""); startupArgs = TextField(""); cleanDesktop = Check("卸载时删除桌面快捷方式", true); cleanStartMenu = Check("卸载时删除开始菜单快捷方式", true); cleanStartup = Check("卸载时删除启动项", true); cleanInstallDir = Check("卸载时删除安装目录（包括用户生成文件）", false);
        theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = Field, ForeColor = TextMain, FlatStyle = FlatStyle.Flat }; theme.Items.AddRange(new object[] { "dark", "light" }); theme.SelectedIndex = 0;
        AddRow(bp, 0, "安装模式", customInstall); AddRow(bp, 1, "基础安装路径", allowInstallPathSelection); AddRow(bp, 2, "系统 Path", addToSystemPath); AddRow(bp, 3, "Path 路径", systemPathValue); AddRow(bp, 4, "桌面快捷方式", desktop); AddRow(bp, 5, "开始菜单", startMenu); AddRow(bp, 6, "启动项", startup); AddRow(bp, 7, "启动项名称", startupName); AddRow(bp, 8, "启动参数", startupArgs); AddRow(bp, 9, "卸载清理", cleanDesktop); AddRow(bp, 10, "", cleanStartMenu); AddRow(bp, 11, "", cleanStartup); AddRow(bp, 12, "", cleanInstallDir); AddRow(bp, 13, "安装界面主题", theme);
    }
    CheckBox Check(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = TextMain, BackColor = Card, FlatStyle = FlatStyle.Flat }; }
    void BuildResourcesPage()
    {
        TabPage external = NewPage("外部资源"); pageTabs.TabPages.Add(external); CardPanel card = new CardPanel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(20) }; external.Controls.Add(card);
        resources = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, BackgroundColor = Field, BorderStyle = BorderStyle.None, GridColor = Line, EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(237, 246, 245), ForeColor = Cyan, SelectionBackColor = Color.FromArgb(237, 246, 245), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) }, DefaultCellStyle = new DataGridViewCellStyle { BackColor = Field, ForeColor = TextMain, SelectionBackColor = Color.FromArgb(218, 241, 238), SelectionForeColor = TextMain }, RowHeadersVisible = false };
        resources.Columns.Add("name", "名称"); resources.Columns.Add("downloadUrl", "下载 URL"); resources.Columns.Add("extractPath", "目标相对路径"); resources.Columns.Add(new DataGridViewCheckBoxColumn { Name = "required", HeaderText = "必选" }); resources.Columns.Add("sha256", "SHA-256（可选）");
        Label cap = LabelText("登记安装时需要下载或解压的教学内容资源", 8.5F, TextMuted); cap.Dock = DockStyle.Top; cap.Padding = new Padding(0, 0, 0, 12);
        Label head = LabelText("课程资源与可选组件", 12F, TextMain); head.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold); head.Dock = DockStyle.Top; head.Padding = new Padding(0, 0, 0, 5);
        card.Controls.Add(resources); card.Controls.Add(cap); card.Controls.Add(head);
    }
    void BuildLogPage()
    {
        TabPage build = NewPage("构建日志"); pageTabs.TabPages.Add(build); CardPanel card = new CardPanel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(20) }; build.Controls.Add(card);
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Card }; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label cap = LabelText("调用本地 build-worker.ps1 并实时显示安装包生成进度", 8.5F, TextMuted); cap.Dock = DockStyle.Top; cap.Padding = new Padding(0, 0, 0, 12);
        Label head = LabelText("构建执行台", 12F, TextMain); head.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold); head.Dock = DockStyle.Top; head.Padding = new Padding(0, 0, 0, 5);
        card.Controls.Add(layout); card.Controls.Add(cap); card.Controls.Add(head);
        Button go = ActionButton("调用 build-worker.ps1 构建安装包", true); go.Width = 290; go.Click += delegate { StartBuild(); }; buildState = LabelText("状态：空闲", 9F, TextMuted); buildState.Padding = new Padding(0, 12, 0, 4); progress = new ProgressBar { Dock = DockStyle.Top, Height = 12, ForeColor = Cyan, BackColor = Field }; logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(241, 247, 247), ForeColor = Color.FromArgb(45, 93, 96), Font = new Font("Consolas", 9F) }; outputLabel = LabelText("输出：", 9F, TextMuted); outputLabel.Padding = new Padding(0, 8, 0, 0);
        layout.Controls.Add(go, 0, 0); layout.Controls.Add(buildState, 0, 1); layout.Controls.Add(progress, 0, 2); layout.Controls.Add(logBox, 0, 3); layout.Controls.Add(outputLabel, 0, 4);
    }
    TableLayoutPanel FormTable() { TableLayoutPanel t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 5, 0, 0), BackColor = Card }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return t; }
    object Get(IDictionary<string, object> d, string key) { return d.ContainsKey(key) && d[key] != null ? d[key] : null; }
    string S(IDictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? "" : Convert.ToString(v); }
    bool B(IDictionary<string, object> d, string key) { object v = Get(d, key); return v != null && Convert.ToBoolean(v); }
    void LoadConfig(string path, bool showMessage)
    {
        try { if (!File.Exists(path)) { if (showMessage) MessageBox.Show("文件不存在：" + path); return; } loadingConfig = true; IDictionary<string, object> d = json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>; productName.Text = S(d,"productName"); version.Text = S(d,"version"); publisher.Text = S(d,"publisher"); subtitle.Text = S(d,"subtitle"); sourceDir.Text = S(d,"sourceDir"); outputDir.Text = S(d,"outputDir"); installPath.Text = S(d,"installPath"); mainExe.Text = S(d,"mainExe"); iconPath.Text = S(d,"iconPath"); customInstall.Checked = !d.ContainsKey("allowCustomInstall") || B(d,"allowCustomInstall"); allowInstallPathSelection.Checked = B(d,"allowInstallPathSelection"); addToSystemPath.Checked = B(d,"addToSystemPath"); systemPathValue.Text = d.ContainsKey("systemPathValue") ? S(d,"systemPathValue") : (S(d,"environmentValue").Length > 0 ? S(d,"environmentValue") : "{app}"); desktop.Checked = B(d,"createDesktopShortcut"); startMenu.Checked = B(d,"createStartMenuShortcut"); startup.Checked = B(d,"createStartupEntry"); startupName.Text = S(d,"startupEntryName"); startupArgs.Text = S(d,"startupArguments"); cleanDesktop.Checked = !d.ContainsKey("cleanupDesktopShortcut") || B(d,"cleanupDesktopShortcut"); cleanStartMenu.Checked = !d.ContainsKey("cleanupStartMenuShortcut") || B(d,"cleanupStartMenuShortcut"); cleanStartup.Checked = !d.ContainsKey("cleanupStartupEntry") || B(d,"cleanupStartupEntry"); cleanInstallDir.Checked = B(d,"cleanupInstallDirectory"); int themeIndex = theme.FindStringExact(S(d,"theme")); theme.SelectedIndex = themeIndex >= 0 ? themeIndex : 0; resources.Rows.Clear(); IEnumerable list = Get(d,"optionalComponents") as IEnumerable; if (list != null) foreach (object item in list) { IDictionary<string, object> r = item as IDictionary<string, object>; if (r != null) resources.Rows.Add(S(r,"name"), S(r,"downloadUrl"), S(r,"extractPath"), B(r,"required"), S(r,"sha256")); } loadingConfig = false; if (showMessage) MessageBox.Show("已导入配置。", Text); } catch (Exception ex) { loadingConfig = false; MessageBox.Show("读取配置失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    Dictionary<string, object> Config()
    {
        List<Dictionary<string, object>> list = new List<Dictionary<string, object>>(); foreach (DataGridViewRow row in resources.Rows) if (!row.IsNewRow) { string name = Convert.ToString(row.Cells["name"].Value ?? ""); string url = Convert.ToString(row.Cells["downloadUrl"].Value ?? ""); if (name.Length > 0 || url.Length > 0) list.Add(new Dictionary<string, object> { {"name",name}, {"downloadUrl",url}, {"extractPath",Convert.ToString(row.Cells["extractPath"].Value ?? "")}, {"required",Convert.ToBoolean(row.Cells["required"].Value ?? false)}, {"sha256",Convert.ToString(row.Cells["sha256"].Value ?? "")} }); }
        return new Dictionary<string, object> { {"productName",productName.Text}, {"version",version.Text}, {"publisher",publisher.Text}, {"subtitle",subtitle.Text}, {"sourceDir",sourceDir.Text}, {"outputDir",outputDir.Text}, {"installPath",installPath.Text}, {"mainExe",mainExe.Text}, {"iconPath",iconPath.Text}, {"allowCustomInstall",customInstall.Checked}, {"allowInstallPathSelection",allowInstallPathSelection.Checked}, {"addToSystemPath",addToSystemPath.Checked}, {"systemPathValue",systemPathValue.Text}, {"createDesktopShortcut",desktop.Checked}, {"createStartMenuShortcut",startMenu.Checked}, {"createStartupEntry",startup.Checked}, {"startupEntryName",startupName.Text}, {"startupArguments",startupArgs.Text}, {"cleanupDesktopShortcut",cleanDesktop.Checked}, {"cleanupStartMenuShortcut",cleanStartMenu.Checked}, {"cleanupStartupEntry",cleanStartup.Checked}, {"cleanupInstallDirectory",cleanInstallDir.Checked}, {"theme",theme.Text}, {"optionalComponents",list} };
    }
    void SaveConfig(string path) { File.WriteAllText(path, json.Serialize(Config()), new UTF8Encoding(false)); }
    void SaveDefaultConfig() { try { SaveConfig(Path.Combine(scriptDir, "build-config.json")); } catch { } }
    void QueueAutoSave() { if (loadingConfig) return; saveTimer.Stop(); saveTimer.Start(); }
    void BindAutoSave()
    {
        saveTimer = new Timer(); saveTimer.Interval = 650; saveTimer.Tick += delegate { saveTimer.Stop(); SaveDefaultConfig(); };
        foreach (Control control in AllControls(this)) { TextBox text = control as TextBox; if (text != null) text.TextChanged += delegate { QueueAutoSave(); }; CheckBox check = control as CheckBox; if (check != null) check.CheckedChanged += delegate { QueueAutoSave(); }; ComboBox combo = control as ComboBox; if (combo != null) combo.SelectedIndexChanged += delegate { QueueAutoSave(); }; DataGridView grid = control as DataGridView; if (grid != null) { grid.CellValueChanged += delegate { QueueAutoSave(); }; grid.RowsAdded += delegate { QueueAutoSave(); }; grid.RowsRemoved += delegate { QueueAutoSave(); }; } }
    }
    IEnumerable<Control> AllControls(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in AllControls(child)) yield return nested; } }
    void ImportConfig() { string path; if (BrowseFile("导入 build-config.json", scriptDir, "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*", out path)) LoadConfig(path, true); }
    void ExportConfig() { string path; if (SaveFile("导出 build-config.json", scriptDir, "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*", "build-config.json", out path)) { SaveConfig(path); MessageBox.Show("配置已导出。", Text); } }
    void ScanDirectory() { try { if (!Directory.Exists(sourceDir.Text)) throw new DirectoryNotFoundException(sourceDir.Text); long bytes = 0; int count = 0; foreach (string f in Directory.GetFiles(sourceDir.Text, "*", SearchOption.AllDirectories)) { count++; bytes += new FileInfo(f).Length; } scanResult.Text = string.Format("{0:N0} 个文件，{1:N2} MB", count, bytes / 1024.0 / 1024.0); } catch (Exception ex) { scanResult.Text = "扫描失败：" + ex.Message; } }
    void StartBuild() { try { if (!Directory.Exists(sourceDir.Text)) { MessageBox.Show("请选择有效的基础程序目录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; } string worker = Path.Combine(scriptDir, "build-worker.ps1"), config = Path.Combine(scriptDir, "build-config.json"), status = Path.Combine(scriptDir, "build-status.json"); if (!File.Exists(worker)) throw new FileNotFoundException("未找到 build-worker.ps1", worker); SaveConfig(config); File.WriteAllText(status, json.Serialize(new Dictionary<string, object> { {"status","starting"}, {"progress",0}, {"log",new string[] { "Starting build process..." }}, {"output",""}, {"error",""} }), new UTF8Encoding(false)); string inno = Environment.GetEnvironmentVariable("INNO_SETUP_PATH") ?? @"C:\Program Files (x86)\Inno Setup 6"; ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + worker + "\" -ConfigFile \"" + config + "\" -StatusFile \"" + status + "\" -ScriptDir \"" + scriptDir + "\" -InnoBinDir \"" + inno + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false; Process.Start(psi); logBox.Clear(); progress.Value = 0; buildState.Text = "状态：构建已启动"; SelectPage(3); statusTimer.Start(); } catch (Exception ex) { MessageBox.Show("无法启动构建：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    void PollStatus() { try { string statusFile = Path.Combine(scriptDir, "build-status.json"); if (!File.Exists(statusFile)) return; IDictionary<string, object> d = json.DeserializeObject(File.ReadAllText(statusFile, Encoding.UTF8)) as IDictionary<string, object>; string state = S(d,"status"); int value = 0; Int32.TryParse(S(d,"progress"), out value); progress.Value = Math.Max(0, Math.Min(100, value)); buildState.Text = "状态：" + state + "（" + value + "%）"; outputLabel.Text = "输出：" + S(d,"output"); logBox.Text = ""; IEnumerable logs = Get(d,"log") as IEnumerable; if (logs != null) foreach (object line in logs) logBox.AppendText(Convert.ToString(line) + Environment.NewLine); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret(); if (state == "done" || state == "error") statusTimer.Stop(); } catch { } }
    [STAThread] public static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new InstallerStudioNative()); }
}