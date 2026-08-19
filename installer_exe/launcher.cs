using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

class Launcher
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PreparationForm());
    }
}

sealed class PreparationForm : Form
{
    private const string ProductName = "__PRODUCT_NAME__";
    private const string ProductVersion = "__PRODUCT_VERSION__";
    private const string ProductSubtitle = "__PRODUCT_SUBTITLE__";
    private readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
    private readonly Label status = new Label();
    private readonly Label percent = new Label();
    private readonly ThinProgressBar progress = new ThinProgressBar();
    private readonly Button closeButton = new Button();
    private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer { Interval = 40 };
    private string tempDir;
    private string payloadPath;
    private string readyFilePath;
    private int targetProgress;
    private int smoothValue;
    private DateTime payloadLaunchTime;
    private DateTime lastPostLaunchStepAt;
    private bool payloadStarted;
    private bool clientReady;

    public PreparationForm()
    {
        Text = "正在准备安装"; ClientSize = new Size(520, 330); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None; BackColor = Color.FromArgb(242, 244, 250); DoubleBuffered = true;
        Region = new Region(RoundedRectangle(new Rectangle(0, 0, Width, Height), 22));
        var card = new Panel { Location = new Point(16, 16), Size = new Size(488, 298), BackColor = Color.FromArgb(255, 255, 255) };
        card.Paint += delegate(object sender, PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var wash = new LinearGradientBrush(card.ClientRectangle, Color.FromArgb(255,255,255), Color.FromArgb(246,249,255), LinearGradientMode.Vertical)) e.Graphics.FillRectangle(wash, card.ClientRectangle); using (var glow = new SolidBrush(Color.FromArgb(26, 10, 132, 255))) e.Graphics.FillEllipse(glow, new Rectangle(374, -48, 156, 156)); };
        card.Region = new Region(RoundedRectangle(new Rectangle(0, 0, card.Width, card.Height), 18)); Controls.Add(card);
        Panel red = new Panel { BackColor = Color.FromArgb(255,95,87), Location = new Point(22,18), Size = new Size(12,12) }; red.Region = new Region(new Rectangle(0,0,12,12));
        Panel yellow = new Panel { BackColor = Color.FromArgb(255,189,46), Location = new Point(40,18), Size = new Size(12,12) }; yellow.Region = new Region(new Rectangle(0,0,12,12));
        Panel green = new Panel { BackColor = Color.FromArgb(40,200,64), Location = new Point(58,18), Size = new Size(12,12) }; green.Region = new Region(new Rectangle(0,0,12,12));
        Button titleClose = new Button { Text = "×", Font = new Font("Segoe UI", 14f), ForeColor = Color.FromArgb(110,110,115), BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat, Location = new Point(438, 8), Size = new Size(36, 30), Cursor = Cursors.Hand };
        titleClose.FlatAppearance.BorderSize = 0; titleClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(241,243,247); titleClose.Click += delegate { Close(); };
        card.Controls.Add(red); card.Controls.Add(yellow); card.Controls.Add(green); card.Controls.Add(titleClose);
        AddLogo(card);
        card.Controls.Add(CreateLabel(ProductName, new Point(36, 126), new Size(376, 30), 18f, FontStyle.Bold, Color.FromArgb(28, 28, 30)));
        card.Controls.Add(CreateLabel(ProductSubtitle, new Point(36, 158), new Size(376, 22), 9.5f, FontStyle.Regular, Color.FromArgb(110, 110, 115)));
        card.Controls.Add(CreateLabel("版本 " + ProductVersion, new Point(36, 181), new Size(376, 22), 9.5f, FontStyle.Regular, Color.FromArgb(142, 142, 147)));
        status.Location = new Point(36, 211); status.Size = new Size(330, 23); status.Font = new Font("Microsoft YaHei UI", 10f); status.ForeColor = Color.FromArgb(58, 58, 60); status.Text = "正在准备安装数据（0%）"; card.Controls.Add(status);
        percent.Location = new Point(385, 209); percent.Size = new Size(62, 25); percent.TextAlign = ContentAlignment.MiddleRight; percent.Font = new Font("Segoe UI", 11f, FontStyle.Bold); percent.ForeColor = Color.FromArgb(10, 132, 255); percent.Text = "0%"; card.Controls.Add(percent);
        progress.Location = new Point(36, 248); progress.Size = new Size(412, 6); progress.Value = 0; card.Controls.Add(progress);
        closeButton.Text = "关闭"; closeButton.Visible = false; closeButton.Location = new Point(370, 258); closeButton.Size = new Size(78, 30); closeButton.FlatStyle = FlatStyle.Flat; closeButton.FlatAppearance.BorderColor = Color.FromArgb(10, 132, 255); closeButton.ForeColor = Color.FromArgb(10, 132, 255); closeButton.Click += delegate { Close(); }; card.Controls.Add(closeButton);
        Shown += delegate { progressTimer.Start(); worker.RunWorkerAsync(); };
        progressTimer.Tick += AdvanceVisualProgress;
        worker.DoWork += ExtractPayload;
        worker.ProgressChanged += UpdateTargetProgress;
        worker.RunWorkerCompleted += ExtractionCompleted;
    }

    private void AddLogo(Panel card)
    {
        Image image = LoadPreparationLogo();
        if (image != null)
        {
            // 原Logo为横向画幅，因此提供更宽的展示区域，避免主体在方形容器中被缩得过小。
            var logo = new PictureBox { Location = new Point(36, 48), Size = new Size(180, 72), SizeMode = PictureBoxSizeMode.Zoom, Image = image, BackColor = Color.Transparent };
            card.Controls.Add(logo);
        }
        else
        {
            var mark = new Label { Location = new Point(36, 48), Size = new Size(72, 72), Text = "↓", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(10, 132, 255), Font = new Font("Segoe UI", 32f, FontStyle.Regular) };
            mark.Region = new Region(RoundedRectangle(new Rectangle(0, 0, 72, 72), 20)); card.Controls.Add(mark);
        }
    }
    private static Image LoadPreparationLogo()
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("preparation-logo");
        if (stream == null) return null;
        using (stream) using (Image raw = Image.FromStream(stream)) return new Bitmap(raw);
    }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; } }
    private static Label CreateLabel(string text, Point location, Size size, float fontSize, FontStyle style, Color color) { return new Label { Text = text, Location = location, Size = size, Font = new Font("Microsoft YaHei UI", fontSize, style), ForeColor = color }; }

    private void ExtractPayload(object sender, DoWorkEventArgs e)
    {
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "setup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tempDir);
            payloadPath = Path.Combine(tempDir, "setup-payload.exe");
            readyFilePath = Path.Combine(Path.GetTempPath(), "installer-ready.flag");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-payload.exe"))
            {
                if (source == null) throw new InvalidDataException("未找到内置安装数据。");
                long total = source.Length, copied = 0, lastCopied = -1; int lastPercent = -1; DateTime lastReport = DateTime.MinValue;
                byte[] buffer = new byte[64 * 1024];
                using (FileStream destination = new FileStream(payloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        destination.Write(buffer, 0, read); copied += read;
                        int value = total == 0 ? 100 : (int)(copied * 100L / total);
                        if (value != lastPercent && (copied - lastCopied >= buffer.Length || value == 100 || (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100))
                        { worker.ReportProgress(value, "正在解压安装数据（" + value + "%）"); lastPercent = value; lastCopied = copied; lastReport = DateTime.UtcNow; }
                    }
                }
            }
        }
        catch (Exception ex) { e.Result = ex; }
    }
    private void UpdateTargetProgress(object sender, ProgressChangedEventArgs e)
    {
        // 外层载荷复制占准备阶段的前 55%，余下进度留给内层安装器启动。
        targetProgress = Math.Max(0, Math.Min(55, e.ProgressPercentage * 55 / 100));
    }
    private void ExtractionCompleted(object sender, RunWorkerCompletedEventArgs e)
    {
        Exception error = e.Result as Exception;
        if (error != null)
        {
            progressTimer.Stop(); status.Text = "安装数据准备失败"; status.ForeColor = Color.FromArgb(184, 72, 66); percent.Text = "错误"; percent.ForeColor = status.ForeColor; progress.Value = 0; closeButton.Visible = true;
            MessageBox.Show(this, "无法准备安装数据。请确认安装包完整后重新下载并运行。\r\n\r\n" + error.Message, "安装未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        // 载荷一写完即启动内层安装器，让其解压过程与进度动画并行。
        targetProgress = Math.Max(targetProgress, 90);
        StartPayload();
    }
    private void AdvanceVisualProgress(object sender, EventArgs e)
    {
        // 内层安装器启动后仍按固定节奏从 90% 持续推进，避免在某个百分比停住。
        if (payloadStarted && !clientReady && smoothValue >= 90 && smoothValue < 99 &&
            (DateTime.Now - lastPostLaunchStepAt).TotalMilliseconds >= 180)
        {
            targetProgress = Math.Max(targetProgress, smoothValue + 1);
            lastPostLaunchStepAt = DateTime.Now;
        }
        if (smoothValue < targetProgress)
        {
            smoothValue = Math.Min(targetProgress, smoothValue + 1);
            progress.Value = smoothValue; percent.Text = smoothValue + "%";
            status.Text = payloadStarted ? "正在初始化客户端安装程序（" + smoothValue + "%）" : "正在准备安装数据（" + smoothValue + "%）";
        }
        if (clientReady && smoothValue >= 100)
        {
            progressTimer.Stop();
            Close();
        }
    }
    private void StartPayload()
    {
        try
        {
            payloadStarted = true;
            payloadLaunchTime = DateTime.Now;
            lastPostLaunchStepAt = payloadLaunchTime;
            if (File.Exists(readyFilePath)) File.Delete(readyFilePath);
            string arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            Process process = Process.Start(new ProcessStartInfo(payloadPath, arguments) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) throw new InvalidOperationException("无法启动安装程序。");
            // 不显示虚假的 100%：等待客户端真正写入就绪标记后再完成交接。
            status.Text = "正在打开客户端安装器";
            System.Windows.Forms.Timer detectionTimer = new System.Windows.Forms.Timer { Interval = 100 };
            detectionTimer.Tick += delegate(object sender, EventArgs e)
            {
                if (!string.IsNullOrEmpty(readyFilePath) && File.Exists(readyFilePath))
                {
                    ((System.Windows.Forms.Timer)sender).Stop(); ((System.Windows.Forms.Timer)sender).Dispose();
                    // 客户端窗口已创建：本帧补足至 100%，下一帧关闭准备界面，避免在 99% 停留。
                    clientReady = true;
                    targetProgress = 100;
                    return;
                }
                if ((DateTime.Now - payloadLaunchTime).TotalSeconds >= 60)
                {
                    ((System.Windows.Forms.Timer)sender).Stop(); ((System.Windows.Forms.Timer)sender).Dispose();
                    status.Text = "客户端安装器未能启动"; status.ForeColor = Color.FromArgb(184, 72, 66); percent.Text = "错误"; percent.ForeColor = status.ForeColor; closeButton.Visible = true;
                }
            };
            detectionTimer.Start();
        }
        catch (Exception ex)
        {
            status.Text = "客户端安装器未能启动"; status.ForeColor = Color.FromArgb(184, 72, 66); percent.Text = "错误"; percent.ForeColor = status.ForeColor; closeButton.Visible = true;
            MessageBox.Show(this, "无法启动安装程序。\r\n\r\n" + ex.Message, "安装未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius) { int diameter = radius * 2; var path = new GraphicsPath(); path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90); path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90); path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path; }
}
sealed class ThinProgressBar : Control
{
    private int value; public int Value { get { return value; } set { value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
    public ThinProgressBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var track = new SolidBrush(Color.FromArgb(225, 230, 240))) e.Graphics.FillRectangle(track, ClientRectangle); using (var fill = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width * value / 100), Height), Color.FromArgb(10, 132, 255), Color.FromArgb(90, 180, 255), LinearGradientMode.Horizontal)) e.Graphics.FillRectangle(fill, 0, 0, Width * value / 100, Height); }
}