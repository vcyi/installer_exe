using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

public class InstallerStudioNative : Form
{
    readonly string scriptDir = AppDomain.CurrentDomain.BaseDirectory;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    TextBox productName, version, publisher, subtitle, sourceDir, outputDir, installPath, mainExe, iconPath, envName, envValue, startupName, startupArgs, scanResult;
    CheckBox customInstall, desktop, startMenu, startup, writeEnv, cleanDesktop, cleanStartMenu, cleanStartup, cleanEnv, cleanInstallDir;
    ComboBox theme;
    DataGridView resources;
    RichTextBox logBox;
    ProgressBar progress;
    Label buildState, outputLabel;
    Timer statusTimer;

    public InstallerStudioNative()
    {
        Text = "Installer Studio Native - Windows 安装包制作台";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 680);
        Size = new Size(1120, 760);
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildUi();
        statusTimer = new Timer(); statusTimer.Interval = 700; statusTimer.Tick += delegate { PollStatus(); };
        LoadConfig(Path.Combine(scriptDir, "build-config.json"), false);
    }

    TextBox TextField(string value) { return new TextBox { Dock = DockStyle.Fill, Text = value ?? "" }; }
    void AddRow(TableLayoutPanel p, int row, string label, Control control)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 8, 8) };
        control.Margin = new Padding(8, 5, 8, 5); control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        p.Controls.Add(l, 0, row); p.Controls.Add(control, 1, row);
    }
    Control BrowseField(TextBox target, bool folder, string filter)
    {
        Panel p = new Panel { Dock = DockStyle.Fill, Height = 28 };
        target.Dock = DockStyle.Fill;
        Button b = new Button { Text = "浏览...", Dock = DockStyle.Right, Width = 72 };
        b.Click += delegate {
            if (folder) { using (FolderBrowserDialog d = new FolderBrowserDialog()) { d.SelectedPath = target.Text; if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath; } }
            else { using (OpenFileDialog d = new OpenFileDialog()) { d.Filter = filter; if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName; } }
        };
        p.Controls.Add(target); p.Controls.Add(b); return p;
    }
    TabPage NewPage(string text) { return new TabPage(text) { Padding = new Padding(12), AutoScroll = true }; }
    void BuildUi()
    {
        MenuStrip menu = new MenuStrip();
        ToolStripMenuItem file = new ToolStripMenuItem("文件");
        file.DropDownItems.Add("导入 build-config.json...", null, delegate { ImportConfig(); });
        file.DropDownItems.Add("导出 build-config.json...", null, delegate { ExportConfig(); });
        file.DropDownItems.Add("保存到默认配置", null, delegate { SaveConfig(Path.Combine(scriptDir, "build-config.json")); });
        menu.Items.Add(file); Controls.Add(menu); MainMenuStrip = menu;
        ToolStrip tools = new ToolStrip(); tools.Dock = DockStyle.Top;
        tools.Items.Add("扫描基础程序目录", null, delegate { ScanDirectory(); });
        tools.Items.Add(new ToolStripSeparator()); tools.Items.Add("开始构建", null, delegate { StartBuild(); });
        Controls.Add(tools);
        TabControl tabs = new TabControl { Dock = DockStyle.Fill }; Controls.Add(tabs);

        TabPage product = NewPage("产品与目录"); tabs.TabPages.Add(product);
        TableLayoutPanel p = FormTable(); product.Controls.Add(p);
        productName = TextField("My Application"); version = TextField("1.0.0"); publisher = TextField(""); subtitle = TextField("安装程序");
        sourceDir = TextField(""); outputDir = TextField(scriptDir); installPath = TextField("C:\\Program Files\\My Application"); mainExe = TextField(""); iconPath = TextField("");
        AddRow(p, 0, "产品名称 *", productName); AddRow(p, 1, "版本", version); AddRow(p, 2, "发布者", publisher); AddRow(p, 3, "副标题", subtitle);
        AddRow(p, 4, "基础程序目录 *", BrowseField(sourceDir, true, "")); AddRow(p, 5, "输出目录", BrowseField(outputDir, true, ""));
        AddRow(p, 6, "默认安装目录", installPath); AddRow(p, 7, "主程序 EXE", mainExe); AddRow(p, 8, "图标文件", BrowseField(iconPath, false, "图标文件 (*.ico)|*.ico|所有文件 (*.*)|*.*"));
        scanResult = TextField("尚未扫描"); scanResult.ReadOnly = true; AddRow(p, 9, "目录扫描", scanResult);

        TabPage behavior = NewPage("安装行为"); tabs.TabPages.Add(behavior);
        TableLayoutPanel bp = FormTable(); behavior.Controls.Add(bp);
        customInstall = new CheckBox { Text = "允许用户自定义安装（路径与可选组件）", Checked = true, AutoSize = true };
        desktop = new CheckBox { Text = "创建桌面快捷方式", Checked = true, AutoSize = true };
        startMenu = new CheckBox { Text = "创建开始菜单快捷方式", Checked = true, AutoSize = true };
        startup = new CheckBox { Text = "创建当前用户启动项", AutoSize = true };
        writeEnv = new CheckBox { Text = "写入当前用户环境变量", AutoSize = true };
        startupName = TextField(""); startupArgs = TextField(""); envName = TextField(""); envValue = TextField("{app}"); theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; theme.Items.AddRange(new object[] { "dark", "light" }); theme.SelectedIndex = 0;
        cleanDesktop = new CheckBox { Text = "卸载时删除桌面快捷方式", Checked = true, AutoSize = true };
        cleanStartMenu = new CheckBox { Text = "卸载时删除开始菜单快捷方式", Checked = true, AutoSize = true };
        cleanStartup = new CheckBox { Text = "卸载时删除启动项", Checked = true, AutoSize = true };
        cleanEnv = new CheckBox { Text = "卸载时删除环境变量", Checked = true, AutoSize = true };
        cleanInstallDir = new CheckBox { Text = "卸载时删除安装目录（包括用户生成文件）", AutoSize = true };
        AddRow(bp, 0, "安装模式", customInstall); AddRow(bp, 1, "桌面快捷方式", desktop); AddRow(bp, 2, "开始菜单", startMenu); AddRow(bp, 3, "启动项", startup); AddRow(bp, 4, "启动项名称", startupName); AddRow(bp, 5, "启动参数", startupArgs); AddRow(bp, 6, "环境变量", writeEnv); AddRow(bp, 7, "变量名称", envName); AddRow(bp, 8, "变量值", envValue); AddRow(bp, 9, "卸载清理", cleanDesktop); AddRow(bp, 10, "", cleanStartMenu); AddRow(bp, 11, "", cleanStartup); AddRow(bp, 12, "", cleanEnv); AddRow(bp, 13, "", cleanInstallDir); AddRow(bp, 14, "安装界面主题", theme);

        TabPage external = NewPage("外部资源"); tabs.TabPages.Add(external);
        resources = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true };
        resources.Columns.Add("name", "名称"); resources.Columns.Add("downloadUrl", "下载 URL"); resources.Columns.Add("extractPath", "解压路径（保留配置）");
        resources.Columns.Add(new DataGridViewCheckBoxColumn { Name = "required", HeaderText = "必选" }); resources.Columns.Add("hash", "哈希（保留配置）"); external.Controls.Add(resources);

        TabPage build = NewPage("构建日志"); tabs.TabPages.Add(build);
        TableLayoutPanel buildLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        buildLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); buildLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); buildLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); buildLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Button go = new Button { Text = "调用 build-worker.ps1 构建安装包", Height = 36, Width = 300, Anchor = AnchorStyles.Left }; go.Click += delegate { StartBuild(); };
        buildState = new Label { Text = "状态：空闲", AutoSize = true, Padding = new Padding(0, 8, 0, 4) }; progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
        logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9F) };
        outputLabel = new Label { Text = "输出：", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        buildLayout.Controls.Add(go, 0, 0); buildLayout.Controls.Add(buildState, 0, 1); buildLayout.Controls.Add(progress, 0, 1); buildLayout.Controls.Add(logBox, 0, 2); buildLayout.Controls.Add(outputLabel, 0, 3); build.Controls.Add(buildLayout);
    }
    TableLayoutPanel FormTable() { TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return t; }
    object Get(IDictionary<string, object> d, string key) { return d.ContainsKey(key) && d[key] != null ? d[key] : null; }
    string S(IDictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? "" : Convert.ToString(v); }
    bool B(IDictionary<string, object> d, string key) { object v = Get(d, key); return v != null && Convert.ToBoolean(v); }
    void LoadConfig(string path, bool showMessage)
    {
        try {
            if (!File.Exists(path)) { if (showMessage) MessageBox.Show("文件不存在：" + path); return; }
            IDictionary<string, object> d = json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
            productName.Text = S(d,"productName"); version.Text = S(d,"version"); publisher.Text = S(d,"publisher"); subtitle.Text = S(d,"subtitle"); sourceDir.Text = S(d,"sourceDir"); outputDir.Text = S(d,"outputDir"); installPath.Text = S(d,"installPath"); mainExe.Text = S(d,"mainExe"); iconPath.Text = S(d,"iconPath");
            customInstall.Checked = !d.ContainsKey("allowCustomInstall") || B(d,"allowCustomInstall"); desktop.Checked = B(d,"createDesktopShortcut"); startMenu.Checked = B(d,"createStartMenuShortcut"); startup.Checked = B(d,"createStartupEntry"); writeEnv.Checked = B(d,"writeEnvVars"); startupName.Text = S(d,"startupEntryName"); startupArgs.Text = S(d,"startupArguments"); envName.Text = S(d,"environmentVariable"); envValue.Text = S(d,"environmentValue");
            cleanDesktop.Checked = !d.ContainsKey("cleanupDesktopShortcut") || B(d,"cleanupDesktopShortcut"); cleanStartMenu.Checked = !d.ContainsKey("cleanupStartMenuShortcut") || B(d,"cleanupStartMenuShortcut"); cleanStartup.Checked = !d.ContainsKey("cleanupStartupEntry") || B(d,"cleanupStartupEntry"); cleanEnv.Checked = !d.ContainsKey("cleanupEnvironmentVariable") || B(d,"cleanupEnvironmentVariable"); cleanInstallDir.Checked = B(d,"cleanupInstallDirectory");
            int themeIndex = theme.FindStringExact(S(d,"theme")); theme.SelectedIndex = themeIndex >= 0 ? themeIndex : 0; resources.Rows.Clear();
            IEnumerable list = Get(d,"optionalComponents") as IEnumerable; if (list != null) foreach (object item in list) { IDictionary<string, object> r = item as IDictionary<string, object>; if (r != null) resources.Rows.Add(S(r,"name"), S(r,"downloadUrl"), S(r,"extractPath"), B(r,"required"), S(r,"hash")); }
            if (showMessage) MessageBox.Show("已导入配置。", Text);
        } catch (Exception ex) { MessageBox.Show("读取配置失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    Dictionary<string, object> Config()
    {
        List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
        foreach (DataGridViewRow row in resources.Rows) if (!row.IsNewRow) { string name = Convert.ToString(row.Cells["name"].Value ?? ""); string url = Convert.ToString(row.Cells["downloadUrl"].Value ?? ""); if (name.Length > 0 || url.Length > 0) list.Add(new Dictionary<string, object> { {"name",name}, {"downloadUrl",url}, {"extractPath",Convert.ToString(row.Cells["extractPath"].Value ?? "")}, {"required",Convert.ToBoolean(row.Cells["required"].Value ?? false)}, {"hash",Convert.ToString(row.Cells["hash"].Value ?? "")} }); }
        return new Dictionary<string, object> { {"productName",productName.Text}, {"version",version.Text}, {"publisher",publisher.Text}, {"subtitle",subtitle.Text}, {"sourceDir",sourceDir.Text}, {"outputDir",outputDir.Text}, {"installPath",installPath.Text}, {"mainExe",mainExe.Text}, {"iconPath",iconPath.Text}, {"allowCustomInstall",customInstall.Checked}, {"createDesktopShortcut",desktop.Checked}, {"createStartMenuShortcut",startMenu.Checked}, {"createStartupEntry",startup.Checked}, {"startupEntryName",startupName.Text}, {"startupArguments",startupArgs.Text}, {"writeEnvVars",writeEnv.Checked}, {"environmentVariable",envName.Text}, {"environmentValue",envValue.Text}, {"cleanupDesktopShortcut",cleanDesktop.Checked}, {"cleanupStartMenuShortcut",cleanStartMenu.Checked}, {"cleanupStartupEntry",cleanStartup.Checked}, {"cleanupEnvironmentVariable",cleanEnv.Checked}, {"cleanupInstallDirectory",cleanInstallDir.Checked}, {"theme",theme.Text}, {"optionalComponents",list} };
    }
    void SaveConfig(string path) { File.WriteAllText(path, json.Serialize(Config()), new UTF8Encoding(false)); }
    void ImportConfig() { using (OpenFileDialog d = new OpenFileDialog()) { d.Filter = "JSON 文件 (*.json)|*.json"; if (d.ShowDialog(this) == DialogResult.OK) LoadConfig(d.FileName, true); } }
    void ExportConfig() { using (SaveFileDialog d = new SaveFileDialog()) { d.Filter = "JSON 文件 (*.json)|*.json"; d.FileName = "build-config.json"; if (d.ShowDialog(this) == DialogResult.OK) { SaveConfig(d.FileName); MessageBox.Show("配置已导出。", Text); } } }
    void ScanDirectory()
    {
        try { if (!Directory.Exists(sourceDir.Text)) throw new DirectoryNotFoundException(sourceDir.Text); long bytes = 0; int count = 0; foreach (string f in Directory.GetFiles(sourceDir.Text, "*", SearchOption.AllDirectories)) { count++; bytes += new FileInfo(f).Length; } scanResult.Text = string.Format("{0:N0} 个文件，{1:N2} MB", count, bytes / 1024.0 / 1024.0); } catch (Exception ex) { scanResult.Text = "扫描失败：" + ex.Message; }
    }
    void StartBuild()
    {
        try {
            if (!Directory.Exists(sourceDir.Text)) { MessageBox.Show("请选择有效的基础程序目录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string worker = Path.Combine(scriptDir, "build-worker.ps1"), config = Path.Combine(scriptDir, "build-config.json"), status = Path.Combine(scriptDir, "build-status.json"); if (!File.Exists(worker)) throw new FileNotFoundException("未找到 build-worker.ps1", worker);
            SaveConfig(config); File.WriteAllText(status, json.Serialize(new Dictionary<string, object> { {"status","starting"}, {"progress",0}, {"log",new string[] { "Starting build process..." }}, {"output",""}, {"error",""} }), new UTF8Encoding(false));
            string inno = Environment.GetEnvironmentVariable("INNO_SETUP_PATH") ?? @"C:\Program Files (x86)\Inno Setup 6";
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + worker + "\" -ConfigFile \"" + config + "\" -StatusFile \"" + status + "\" -ScriptDir \"" + scriptDir + "\" -InnoBinDir \"" + inno + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false; Process.Start(psi);
            logBox.Clear(); progress.Value = 0; buildState.Text = "状态：构建已启动"; statusTimer.Start();
        } catch (Exception ex) { MessageBox.Show("无法启动构建：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    void PollStatus()
    {
        try { string statusFile = Path.Combine(scriptDir, "build-status.json"); if (!File.Exists(statusFile)) return; IDictionary<string, object> d = json.DeserializeObject(File.ReadAllText(statusFile, Encoding.UTF8)) as IDictionary<string, object>; string state = S(d,"status"); int value = 0; Int32.TryParse(S(d,"progress"), out value); progress.Value = Math.Max(0, Math.Min(100, value)); buildState.Text = "状态：" + state + "（" + value + "%）"; outputLabel.Text = "输出：" + S(d,"output"); logBox.Text = ""; IEnumerable logs = Get(d,"log") as IEnumerable; if (logs != null) foreach (object line in logs) logBox.AppendText(Convert.ToString(line) + Environment.NewLine); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret(); if (state == "done" || state == "error") statusTimer.Stop(); } catch { }
    }
    [STAThread] public static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new InstallerStudioNative()); }
}
