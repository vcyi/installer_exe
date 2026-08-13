using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

class Launcher
{
    [STAThread]
    static void Main()
    {
        try
        {
            // Extract embedded Inno Setup installer to temp file
            string tempDir = Path.Combine(Path.GetTempPath(), "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, "setup-payload.exe");

            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("setup-payload.exe"))
            {
                if (stream == null)
                {
                    MessageBox.Show("安装数据损坏，请重新下载。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                using (var file = File.Create(tempPath))
                {
                    stream.CopyTo(file);
                }
            }

            // Run the Inno Setup installer in very silent mode
            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc.WaitForExit();

            // Clean up temp file
            try
            {
                proc.Dispose();
                File.Delete(tempPath);
                Directory.Delete(tempDir, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装程序启动失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
