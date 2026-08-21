using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class Launcher
{
    [STAThread]
    static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new PreparationForm()); }
}

sealed class PreparationForm : Form
{
    private const string ProductName = "__PRODUCT_NAME__";
    private const string ProductVersion = "__PRODUCT_VERSION__";
    private const string ProductSubtitle = "__PRODUCT_SUBTITLE__";
    private const string ConfigJson = @"__LAUNCHER_CONFIG_JSON__";
    private const int FooterSize = 32;
    private static readonly byte[] FooterMagic = new byte[] { (byte)'N',(byte)'C',(byte)'I',(byte)'A',(byte)'P',(byte)'A',(byte)'Y',(byte)'2' };
    private readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
    private readonly Timer progressTimer = new Timer { Interval = 40 };
    private readonly Timer sessionTimer = new Timer { Interval = 180 };
    private readonly Panel card = new Panel();
    private readonly Label status = new Label();
    private readonly Label downloadDetail = new Label();
    private readonly Label percent = new Label();
    private readonly ThinProgressBar progress = new ThinProgressBar();
    private readonly Button closeButton = new Button();
    private Dictionary<string, object> config;
    private string tempDir, payloadPath, sessionPath;
    private int targetProgress, smoothValue;
    private Process payloadProcess;
    private bool payloadStarted, completedPageShown, cleanupStarted;

    public PreparationForm()
    {
        config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ConfigJson);
        Text = "安装 " + ProductName; ClientSize = new Size(520, 390); StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadProductIcon();
        FormBorderStyle = FormBorderStyle.None; BackColor = Color.FromArgb(242, 244, 250); DoubleBuffered = true;
        Region = new Region(RoundedRectangle(new Rectangle(0, 0, Width, Height), 22));
        card.Location = new Point(16, 16); card.Size = new Size(488, 358); card.BackColor = Color.White;
        card.Paint += delegate(object sender, PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var wash = new LinearGradientBrush(card.ClientRectangle, Color.White, Color.FromArgb(246,249,255), LinearGradientMode.Vertical)) e.Graphics.FillRectangle(wash, card.ClientRectangle); };
        card.Region = new Region(RoundedRectangle(new Rectangle(0, 0, card.Width, card.Height), 18)); Controls.Add(card);
        Shown += delegate { ShowSelectionPage(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (worker.IsBusy) { e.Cancel = true; status.Text = "正在准备安装数据，请稍候"; } };
        progressTimer.Tick += AdvanceVisualProgress; sessionTimer.Tick += PollSession;
        worker.DoWork += ExtractPayload; worker.ProgressChanged += UpdateTargetProgress; worker.RunWorkerCompleted += ExtractionCompleted;
    }

    private void ShowSelectionPage()
    {
        card.Controls.Clear(); AddHeader("安装选项"); AddLogo(card, new Point(36, 44), new Size(150, 58));
        card.Controls.Add(CreateLabel(ProductName, new Point(36, 108), new Size(360, 25), 15f, FontStyle.Bold, Color.FromArgb(28,28,30)));
        card.Controls.Add(CreateLabel("版本 " + ProductVersion + "  ·  " + ProductSubtitle, new Point(36, 134), new Size(400, 20), 9f, FontStyle.Regular, Color.FromArgb(110,110,115)));
        string runtimeNotice = GetString(config, "runtimeNotice", "");
        if (!string.IsNullOrWhiteSpace(runtimeNotice)) card.Controls.Add(CreateLabel(runtimeNotice, new Point(36, 157), new Size(412, 32), 8.2f, FontStyle.Regular, Color.FromArgb(90,90,96)));
        bool chooseDir = GetBool(config, "allowInstallDirSelection", true);
        string defaultDir = GetString(config, "installPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName));
        card.Controls.Add(CreateLabel("安装目录", new Point(36, 191), new Size(180, 18), 9.5f, FontStyle.Bold, Color.FromArgb(58,58,60)));
        var directory = new TextBox { Location = new Point(36, 211), Size = new Size(326, 26), Text = defaultDir, ReadOnly = !chooseDir, Font = new Font("Microsoft YaHei UI", 9f), BorderStyle = BorderStyle.FixedSingle };
        card.Controls.Add(directory);
        var browse = new Button { Text = "浏览...", Location = new Point(371, 210), Size = new Size(77, 28), Enabled = chooseDir, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(10,132,255) };
        browse.FlatAppearance.BorderColor = Color.FromArgb(190,210,235); browse.Click += delegate { string selectedPath; if (ShellFolderDialog.TrySelectFolder(this.Handle, directory.Text, out selectedPath)) directory.Text = selectedPath; }; card.Controls.Add(browse);
        var choices = GetComponents();
        string componentTitle = GetString(config, "componentTitle", "");
        string componentNotice = GetString(config, "componentNotice", "");
        int listTop = 250;
        if (!string.IsNullOrWhiteSpace(componentTitle))
        {
            int titleHeight = MeasureTextHeight(componentTitle, 10.5f, FontStyle.Bold, 412);
            card.Controls.Add(CreateLabel(componentTitle, new Point(36, listTop), new Size(412, titleHeight), 10.5f, FontStyle.Bold, Color.FromArgb(58,58,60)));
            listTop += titleHeight + 6;
        }
        if (!string.IsNullOrWhiteSpace(componentNotice))
        {
            int noticeHeight = MeasureTextHeight(componentNotice, 8.2f, FontStyle.Regular, 412);
            card.Controls.Add(CreateLabel(componentNotice, new Point(36, listTop), new Size(412, noticeHeight), 8.2f, FontStyle.Regular, Color.FromArgb(120,78,0)));
            listTop += noticeHeight + 6;
        }
        var selected = new List<CheckBox>();
        int availableListHeight = Math.Max(22, Screen.FromControl(this).WorkingArea.Height - 32 - listTop - 46);
        float componentFontSize; int listHeight;
        GetComponentLayout(choices, 412, availableListHeight, out componentFontSize, out listHeight);
        var componentPanel = new Panel { Location = new Point(36, listTop), Size = new Size(412, listHeight), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AutoScroll = false };
        if (choices.Count == 0) componentPanel.Controls.Add(CreateLabel("未配置可选组件", new Point(8, 2), new Size(380, 22), 9.5f, FontStyle.Bold, Color.FromArgb(110,110,115)));
        else {
            int y = 2; int textWidth = 378;
            foreach (var choice in choices) {
                int rowHeight = MeasureComponentRow(choice.Name, componentFontSize, textWidth);
                var check = new CheckBox { Location = new Point(7, y + Math.Max(0, (rowHeight - 16) / 2)), Size = new Size(16, 16), Checked = choice.Required, Tag = choice, AutoSize = false };
                var text = CreateLabel(choice.Name, new Point(27, y), new Size(textWidth, rowHeight), componentFontSize, FontStyle.Regular, Color.FromArgb(45,45,48));
                text.AutoEllipsis = false; text.Click += delegate(object sender, EventArgs e) { check.Checked = !check.Checked; };
                componentPanel.Controls.Add(check); componentPanel.Controls.Add(text); selected.Add(check); y += rowHeight;
            }
        }
        card.Controls.Add(componentPanel);
        int cardHeight = listTop + listHeight + 46;
        ClientSize = new Size(520, cardHeight + 32); Region = new Region(RoundedRectangle(new Rectangle(0, 0, Width, Height), 22));
        card.Size = new Size(488, cardHeight); card.Region = new Region(RoundedRectangle(new Rectangle(0, 0, card.Width, card.Height), 18));
        var start = new Button { Text = "开始安装", Location = new Point(348, cardHeight - 40), Size = new Size(100, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(10,132,255), ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) };
        start.FlatAppearance.BorderSize = 0; start.Click += delegate { try { var names = new List<string>(); foreach (CheckBox check in selected) if (check.Checked) names.Add(((ComponentChoice)check.Tag).Name); BeginInstallation(new InstallSelection { InstallDir = ValidateInstallDir(directory.Text), Components = names }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "安装配置无效", MessageBoxButtons.OK, MessageBoxIcon.Error); } }; card.Controls.Add(start);
    }

    private void GetComponentLayout(List<ComponentChoice> choices, int width, int availableHeight, out float fontSize, out int listHeight)
    {
        fontSize = 9f; listHeight = 22;
        for (; fontSize >= 4.5f; fontSize -= 0.5f) { int total = 4; foreach (var choice in choices) total += MeasureComponentRow(choice.Name, fontSize, width - 34); if (total <= availableHeight) { listHeight = Math.Max(22, total); return; } }
        fontSize = 4.5f; int compactTotal = 4; foreach (var choice in choices) compactTotal += MeasureComponentRow(choice.Name, fontSize, width - 34); listHeight = Math.Max(22, compactTotal);
    }
    private int MeasureComponentRow(string name, float fontSize, int textWidth)
    {
        using (var font = new Font("Microsoft YaHei UI", fontSize)) using (var graphics = CreateGraphics()) { SizeF measured = graphics.MeasureString(name, font, textWidth, StringFormat.GenericTypographic); return Math.Max((int)Math.Ceiling(measured.Height) + 4, Math.Max(16, (int)Math.Ceiling(fontSize + 7))); }
    }
    private int MeasureTextHeight(string text, float fontSize, FontStyle style, int textWidth)
    {
        using (var font = new Font("Microsoft YaHei UI", fontSize, style)) using (var graphics = CreateGraphics()) { SizeF measured = graphics.MeasureString(text ?? "", font, textWidth, StringFormat.GenericTypographic); return Math.Max((int)Math.Ceiling(measured.Height) + 2, (int)Math.Ceiling(fontSize + 7)); }
    }

    private void BeginInstallation(InstallSelection selection)
    {
        try { tempDir = Path.Combine(Path.GetTempPath(), "setup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tempDir); sessionPath = Path.Combine(tempDir, "session.json"); var session = new Dictionary<string, object>(); session["sessionId"] = Path.GetFileName(tempDir).Substring(6); session["state"] = "prepared"; session["progress"] = 0; session["message"] = "已创建安装会话"; session["installDir"] = selection.InstallDir; session["selectedComponents"] = selection.Components; session["createdUtc"] = DateTime.UtcNow.ToString("o"); File.WriteAllText(sessionPath, new JavaScriptSerializer().Serialize(session)); ShowPreparationPage(); progressTimer.Start(); worker.RunWorkerAsync(); }
        catch (Exception ex) { MessageBox.Show(this, "无法创建安装会话。\r\n\r\n" + ex.Message, "安装未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowPreparationPage()
    {
        card.Controls.Clear(); AddHeader("正在安装"); AddLogo(card, new Point(36, 48), new Size(180, 72));
        card.Controls.Add(CreateLabel(ProductName, new Point(36, 126), new Size(376, 30), 18f, FontStyle.Bold, Color.FromArgb(28,28,30)));
        card.Controls.Add(CreateLabel(ProductSubtitle, new Point(36, 158), new Size(376, 22), 9.5f, FontStyle.Regular, Color.FromArgb(110,110,115)));
        status.Location = new Point(36, 204); status.Size = new Size(350, 23); status.Font = new Font("Microsoft YaHei UI", 10f); status.ForeColor = Color.FromArgb(58,58,60); status.Text = "正在准备安装数据（0%）"; card.Controls.Add(status);
        downloadDetail.Location = new Point(36, 228); downloadDetail.Size = new Size(350, 18); downloadDetail.Font = new Font("Microsoft YaHei UI", 8.5f); downloadDetail.ForeColor = Color.FromArgb(110,110,115); downloadDetail.Text = ""; card.Controls.Add(downloadDetail);
        percent.Location = new Point(385, 202); percent.Size = new Size(62, 25); percent.TextAlign = ContentAlignment.MiddleRight; percent.Font = new Font("Segoe UI", 11f, FontStyle.Bold); percent.ForeColor = Color.FromArgb(10,132,255); percent.Text = "0%"; card.Controls.Add(percent);
        progress.Location = new Point(36, 255); progress.Size = new Size(412, 6); card.Controls.Add(progress);
        closeButton.Text = "关闭"; closeButton.Visible = false; closeButton.Location = new Point(370, 292); closeButton.Size = new Size(78,30); closeButton.Click += delegate { Close(); }; card.Controls.Add(closeButton);
    }

    private void AddHeader(string title) { var close = new Button { Text = "×", Font = new Font("Segoe UI", 14f), ForeColor = Color.FromArgb(110,110,115), BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat, Location = new Point(438, 8), Size = new Size(36, 30), Cursor = Cursors.Hand }; close.FlatAppearance.BorderSize = 0; close.Click += delegate { Close(); }; card.Controls.Add(close); card.Controls.Add(CreateLabel(title, new Point(36, 14), new Size(250, 24), 10f, FontStyle.Bold, Color.FromArgb(58,58,60))); }
    private string ValidateInstallDir(string path) { if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请选择安装目录。"); string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.Length < root.Length + 4) throw new InvalidDataException("不能安装到磁盘根目录或过浅目录。"); return full; }
    private List<ComponentChoice> GetComponents() { var list = new List<ComponentChoice>(); object raw; if (!config.TryGetValue("optionalComponents", out raw)) return list; var values = raw as ArrayList; if (values == null) return list; foreach (object item in values) { var data = item as Dictionary<string, object>; if (data == null || !GetBool(data, "enabled", false)) continue; string name = GetString(data, "name", ""); if (!string.IsNullOrWhiteSpace(name)) list.Add(new ComponentChoice { Name = name, Required = GetBool(data, "required", false) }); } return list; }
    private Icon LoadProductIcon() { Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("installer-product-icon.ico"); if (stream != null) { using (stream) return new Icon(stream); } string iconPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "installer-product-icon.ico"); if (File.Exists(iconPath)) return new Icon(iconPath); return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
    private void AddLogo(Panel parent, Point location, Size size) { Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("preparation-logo"); if (stream != null) { using (stream) using (Image raw = Image.FromStream(stream)) parent.Controls.Add(new PictureBox { Location = location, Size = size, SizeMode = PictureBoxSizeMode.Zoom, Image = new Bitmap(raw), BackColor = Color.Transparent }); } else parent.Controls.Add(new Label { Location = location, Size = new Size(72,72), Text = "↓", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(10,132,255), Font = new Font("Segoe UI",32f) }); }
    private static Label CreateLabel(string text, Point location, Size size, float fontSize, FontStyle style, Color color) { return new Label { Text = text, Location = location, Size = size, Font = new Font("Microsoft YaHei UI", fontSize, style), ForeColor = color }; }

    private void ExtractPayload(object sender, DoWorkEventArgs e) { try { payloadPath = Path.Combine(tempDir, "setup-payload.exe"); using (FileStream source = new FileStream(Application.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (source.Length < FooterSize) throw new InvalidDataException("安装包不完整：缺少载荷信息。"); source.Seek(-FooterSize, SeekOrigin.End); byte[] footer = new byte[FooterSize]; if (source.Read(footer,0,footer.Length) != footer.Length) throw new InvalidDataException("无法读取载荷信息。"); for (int i=0;i<FooterMagic.Length;i++) if (footer[i] != FooterMagic[i]) throw new InvalidDataException("安装包载荷标识无效。"); long offset = BitConverter.ToInt64(footer,12), total = BitConverter.ToInt64(footer,20); if (offset < 0 || total <= 0 || offset > source.Length-FooterSize || total > source.Length-FooterSize-offset) throw new InvalidDataException("安装包载荷长度无效。"); source.Seek(offset,SeekOrigin.Begin); long copied=0; int lastPercent=-1; byte[] buffer=new byte[64*1024]; using (FileStream destination=new FileStream(payloadPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { while(copied<total) { int wanted=(int)Math.Min(buffer.Length,total-copied); int read=source.Read(buffer,0,wanted); if(read<=0) throw new EndOfStreamException("安装数据被截断。"); destination.Write(buffer,0,read); copied+=read; int value=(int)(copied*100L/total); if(value!=lastPercent){ worker.ReportProgress(value); lastPercent=value; } } } } } catch(Exception ex) { e.Result=ex; } }
    private void UpdateTargetProgress(object sender, ProgressChangedEventArgs e) { targetProgress=Math.Max(0,Math.Min(55,e.ProgressPercentage*55/100)); }
    private void ExtractionCompleted(object sender, RunWorkerCompletedEventArgs e) { Exception error=e.Result as Exception; if(error!=null){ ShowFailure("安装数据准备失败", error.Message); return; } targetProgress=Math.Max(targetProgress,60); StartPayload(); }
    private void StartPayload() { try { payloadStarted=true; var session = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(sessionPath)); string installDir=Convert.ToString(session["installDir"]); string args="/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /InstallDir="+Quote(installDir)+" /Session="+Quote(sessionPath); payloadProcess=Process.Start(new ProcessStartInfo(payloadPath,args){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden,CreateNoWindow=true}); if(payloadProcess==null)throw new InvalidOperationException("无法启动安装程序。"); sessionTimer.Start(); } catch(Exception ex) { ShowFailure("客户端安装器未能启动", ex.Message); } }
    private void PollSession(object sender, EventArgs e) { Dictionary<string, object> session = ReadSession(); string state = GetString(session, "state", ""); int sessionProgress = GetInt(session, "progress", 0); string message = GetString(session, "message", "正在等待安装完成"); string stage = GetString(session, "downloadStage", ""); long downloaded = GetLong(session, "downloadedBytes", 0), total = GetLong(session, "totalBytes", 0), speed = GetLong(session, "speedBytesPerSecond", 0); string component = GetString(session, "componentName", ""); int componentIndex = GetInt(session, "componentIndex", 0), componentCount = GetInt(session, "componentCount", 0); targetProgress = Math.Max(targetProgress, Math.Min(95, 55 + sessionProgress * 40 / 100)); if (string.Equals(stage, "downloading", StringComparison.OrdinalIgnoreCase) && !completedPageShown) { status.Text = "正在下载" + (string.IsNullOrWhiteSpace(component) ? "组件" : "组件（" + componentIndex + "/" + componentCount + "）: " + component); downloadDetail.Text = FormatDownloadDetail(downloaded, total, speed); if (total > 0) targetProgress = Math.Max(targetProgress, Math.Min(95, 55 + (componentCount > 0 ? ((componentIndex - 1) * 40 + (int)(40L * Math.Min(downloaded, total) / total)) / componentCount : (int)(40L * Math.Min(downloaded, total) / total)))); } else { if (!string.IsNullOrWhiteSpace(message) && !completedPageShown) status.Text = message; downloadDetail.Text = ""; } bool exited = payloadProcess != null && payloadProcess.HasExited; if (string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase) && exited) { sessionTimer.Stop(); targetProgress=100; StartIndependentCleanup(); ShowCompletedPage(); return; } if ((string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase) && exited) || (exited && !string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase))) { sessionTimer.Stop(); ShowFailure("安装未完成", GetString(session, "error", "客户端安装器在完成安装前退出。")); } }
    private static string FormatDownloadDetail(long downloaded, long total, long speed) { string rate = speed > 0 ? " · " + FormatBytes(speed) + "/s" : ""; return total > 0 ? "已下载 " + FormatBytes(downloaded) + " / " + FormatBytes(total) + rate : "已下载 " + FormatBytes(downloaded) + rate; }
    private static string FormatBytes(long value) { string[] units = { "B", "KB", "MB", "GB", "TB" }; double size = Math.Max(0, value); int unit = 0; while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; } return size.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit]; }
    private void StartIndependentCleanup() { if (cleanupStarted || string.IsNullOrEmpty(tempDir)) return; cleanupStarted=true; string escaped = tempDir.Replace("'", "''"); string command = "$session='" + escaped + "'; Start-Sleep -Milliseconds 250; for($attempt=1;$attempt -le 30 -and (Test-Path -LiteralPath $session);$attempt++){ Remove-Item -LiteralPath $session -Recurse -Force -ErrorAction SilentlyContinue; if(Test-Path -LiteralPath $session){ Start-Sleep -Seconds 1 } }; if(Test-Path -LiteralPath $session){ $diagnostic=Join-Path $session 'cleanup-timeout-diagnostic.txt'; [System.IO.File]::WriteAllText($diagnostic, ('Cleanup timed out after 30 attempts at '+[DateTime]::UtcNow.ToString('o')+'. Session state was completed and payload process had exited.'), [System.Text.Encoding]::UTF8) }"; try { Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command.Replace("\"", "\\\"") + "\"") { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden }); } catch { } }
    private Dictionary<string, object> ReadSession() { try { if (!string.IsNullOrEmpty(sessionPath) && File.Exists(sessionPath)) return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(sessionPath)); } catch { } return new Dictionary<string, object>(); }
    private void AdvanceVisualProgress(object sender, EventArgs e) { if(smoothValue<targetProgress){smoothValue=Math.Min(targetProgress,smoothValue+1);progress.Value=smoothValue;percent.Text=smoothValue+"%";} }
    private void ShowCompletedPage() { if (completedPageShown) return; completedPageShown=true; progressTimer.Stop(); card.Controls.Clear(); AddHeader("安装完成"); AddLogo(card, new Point(36,48), new Size(150,58)); card.Controls.Add(CreateLabel("安装完成", new Point(36,126), new Size(360,32), 20f, FontStyle.Bold, Color.FromArgb(28,28,30))); card.Controls.Add(CreateLabel(ProductName + " 已成功安装到您的计算机。", new Point(36,164), new Size(390,26), 10f, FontStyle.Regular, Color.FromArgb(110,110,115))); var done = new Button { Text="完成", Location=new Point(370,300), Size=new Size(78,30), FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(10,132,255), ForeColor=Color.White }; done.FlatAppearance.BorderSize=0; done.Click += delegate { Close(); }; card.Controls.Add(done); }
    private void ShowFailure(string title, string detail) { progressTimer.Stop(); sessionTimer.Stop(); status.Text=title; percent.Text="错误"; closeButton.Visible=true; MessageBox.Show(this, detail, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    private static string Quote(string value){return "\""+(value??"").Replace("\"","\\\"")+"\"";} private static string GetString(Dictionary<string,object> d,string key,string def){object v;return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToString(v):def;} private static int GetInt(Dictionary<string,object> d,string key,int def){object v; try { return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToInt32(v):def; } catch { return def; }} private static long GetLong(Dictionary<string,object> d,string key,long def){object v; try { return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToInt64(v):def; } catch { return def; }} private static bool GetBool(Dictionary<string,object> d,string key,bool def){object v;return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToBoolean(v):def;} private static GraphicsPath RoundedRectangle(Rectangle b,int r){int d=r*2;var p=new GraphicsPath();p.AddArc(b.X,b.Y,d,d,180,90);p.AddArc(b.Right-d,b.Y,d,d,270,90);p.AddArc(b.Right-d,b.Bottom-d,d,d,0,90);p.AddArc(b.X,b.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
}
sealed class ShellFolderDialog
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_DONTADDTORECENT = 0x02000000;

    public static bool TrySelectFolder(IntPtr owner, string initialPath, out string selectedPath)
    {
        selectedPath = null;
        IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            uint options; dialog.GetOptions(out options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_DONTADDTORECENT);
            dialog.SetTitle("选择安装目录");
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                IShellItem folder;
                if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out folder) == 0) dialog.SetFolder(folder);
            }
            if (dialog.Show(owner) != 0) return false;
            IShellItem result; dialog.GetResult(out result);
            IntPtr path; result.GetDisplayName(SIGDN.FILESYSPATH, out path);
            try { selectedPath = Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
            return !string.IsNullOrWhiteSpace(selectedPath);
        }
        finally { Marshal.FinalReleaseComObject(dialog); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem shellItem);
    private enum SIGDN : uint { FILESYSPATH = 0x80058000 }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv); void GetParent(out IShellItem ppsi); void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName); void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs); int Compare(IShellItem psi, uint hint, out int piOrder); }
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog { [PreserveSig] int Show(IntPtr parent); void SetFileTypes(uint count, IntPtr filters); void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index); void Advise(IntPtr events, out uint cookie); void Unadvise(uint cookie); void SetOptions(uint options); void GetOptions(out uint options); void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder); void GetFolder(out IShellItem folder); void GetCurrentSelection(out IShellItem item); void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name); void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void GetResult(out IShellItem item); void AddPlace(IShellItem item, uint placement); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension); void Close(int hr); void SetClientGuid(ref Guid guid); void ClearClientData(); void SetFilter(IntPtr filter); void GetResults(out IntPtr results); void GetSelectedItems(out IntPtr items); }
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }
}
sealed class InstallSelection { public string InstallDir; public List<string> Components; }
sealed class ComponentChoice { public string Name { get; set; } public bool Required { get; set; } }
sealed class ThinProgressBar : Control { private int value; public int Value {get{return value;}set{this.value=Math.Max(0,Math.Min(100,value));Invalidate();}} public ThinProgressBar(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);} protected override void OnPaint(PaintEventArgs e){using(var track=new SolidBrush(Color.FromArgb(225,230,240)))e.Graphics.FillRectangle(track,ClientRectangle);using(var fill=new SolidBrush(Color.FromArgb(10,132,255)))e.Graphics.FillRectangle(fill,0,0,Width*value/100,Height);} }
