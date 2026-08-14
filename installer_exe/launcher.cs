using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
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
    private readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
    private readonly Label status = new Label();
    private readonly Label percent = new Label();
    private readonly ThinProgressBar progress = new ThinProgressBar();
    private readonly Button closeButton = new Button();
    private string tempDir;
    private string tempPath;

    public PreparationForm()
    {
        Text = "正在准备安装";
        ClientSize = new Size(500, 310);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(250, 247, 241);
        DoubleBuffered = true;
        Region = new Region(RoundedRectangle(new Rectangle(0, 0, Width, Height), 18));

        var card = new Panel { Location = new Point(22, 22), Size = new Size(456, 266), BackColor = Color.White };
        card.Paint += delegate(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(246, 252, 251)))
                e.Graphics.FillEllipse(brush, new Rectangle(365, -36, 126, 126));
        };
        card.Region = new Region(RoundedRectangle(new Rectangle(0, 0, card.Width, card.Height), 14));
        Controls.Add(card);

        var mark = new Label { Location = new Point(34, 35), Size = new Size(40, 40), Text = "↓", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(19, 157, 151), Font = new Font("Segoe UI", 22f, FontStyle.Regular) };
        mark.Region = new Region(RoundedRectangle(new Rectangle(0, 0, 40, 40), 12));
        card.Controls.Add(mark);
        card.Controls.Add(CreateLabel("智慧教学系统", new Point(34, 91), new Size(330, 30), 18f, FontStyle.Bold, Color.FromArgb(35, 52, 57)));
        card.Controls.Add(CreateLabel("教师端安装程序", new Point(34, 123), new Size(340, 25), 10f, FontStyle.Regular, Color.FromArgb(116, 130, 133)));

        status.Location = new Point(34, 173); status.Size = new Size(290, 23);
        status.Font = new Font("Microsoft YaHei UI", 10f); status.ForeColor = Color.FromArgb(54, 73, 77);
        status.Text = "正在解压安装数据";
        card.Controls.Add(status);
        percent.Location = new Point(365, 171); percent.Size = new Size(56, 25); percent.TextAlign = ContentAlignment.MiddleRight;
        percent.Font = new Font("Segoe UI", 11f, FontStyle.Bold); percent.ForeColor = Color.FromArgb(15, 141, 136); percent.Text = "0%";
        card.Controls.Add(percent);
        progress.Location = new Point(34, 207); progress.Size = new Size(388, 5); progress.Value = 0;
        card.Controls.Add(progress);
        card.Controls.Add(CreateLabel("请稍候，安装数据正在安全准备中", new Point(34, 225), new Size(380, 24), 9f, FontStyle.Regular, Color.FromArgb(150, 160, 161)));

        closeButton.Text = "关闭"; closeButton.Visible = false; closeButton.Location = new Point(346, 218); closeButton.Size = new Size(76, 30);
        closeButton.FlatStyle = FlatStyle.Flat; closeButton.FlatAppearance.BorderColor = Color.FromArgb(19, 157, 151); closeButton.ForeColor = Color.FromArgb(15, 141, 136);
        closeButton.Click += delegate { Close(); }; card.Controls.Add(closeButton);
        Shown += delegate { worker.RunWorkerAsync(); };
        worker.DoWork += ExtractAndRun;
        worker.ProgressChanged += UpdateProgress;
        worker.RunWorkerCompleted += Completed;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; } // CS_DROPSHADOW
    }

    private static Label CreateLabel(string text, Point location, Size size, float fontSize, FontStyle style, Color color)
    {
        return new Label { Text = text, Location = location, Size = size, Font = new Font("Microsoft YaHei UI", fontSize, style), ForeColor = color };
    }

    private void ExtractAndRun(object sender, DoWorkEventArgs e)
    {
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            tempPath = Path.Combine(tempDir, "setup-payload.exe");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-payload.exe"))
            {
                if (source == null) throw new InvalidDataException("未找到内置安装数据。");
                long total = source.Length, copied = 0;
                byte[] buffer = new byte[128 * 1024];
                using (FileStream destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        destination.Write(buffer, 0, read);
                        copied += read;
                        worker.ReportProgress(total == 0 ? 100 : (int)(copied * 100L / total), "正在解压安装数据");
                    }
                }
            }
            worker.ReportProgress(100, "正在启动安装程序");
            using (Process process = Process.Start(new ProcessStartInfo(tempPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                if (process == null) throw new InvalidOperationException("无法启动安装程序。");
                process.WaitForExit();
            }
        }
        catch (Exception ex) { e.Result = ex; }
        finally
        {
            try { if (!String.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    private void UpdateProgress(object sender, ProgressChangedEventArgs e)
    {
        progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
        percent.Text = progress.Value + "%";
        status.Text = e.UserState as string ?? "正在准备安装";
    }

    private void Completed(object sender, RunWorkerCompletedEventArgs e)
    {
        Exception error = e.Result as Exception;
        if (error == null) { Close(); return; }
        status.Text = "安装数据准备失败";
        status.ForeColor = Color.FromArgb(184, 72, 66);
        percent.Text = "错误";
        percent.ForeColor = status.ForeColor;
        progress.Value = 0;
        closeButton.Visible = true;
        MessageBox.Show(this, "无法准备安装数据。请确认安装包完整后重新下载并运行。\r\n\r\n" + error.Message, "安装未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2; var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90); path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
}

sealed class ThinProgressBar : Control
{
    private int value = 0;
    public int Value { get { return value; } set { value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
    public ThinProgressBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = new SolidBrush(Color.FromArgb(226, 239, 237))) e.Graphics.FillRectangle(track, ClientRectangle);
        using (var fill = new SolidBrush(Color.FromArgb(19, 157, 151))) e.Graphics.FillRectangle(fill, 0, 0, Width * value / 100, Height);
    }
}
