using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace InstallerRuntimeLauncher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                string appRoot = Path.GetDirectoryName(Application.ExecutablePath);
                string configPath = Path.Combine(appRoot, "runtime-launcher.json");
                if (!File.Exists(configPath))
                    throw new FileNotFoundException("缺少运行时启动器配置文件。", configPath);

                var config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
                string mainExe = GetString(config, "mainExe");
                if (string.IsNullOrWhiteSpace(mainExe) || Path.IsPathRooted(mainExe))
                    throw new InvalidDataException("运行时启动器主程序配置无效。");

                string root = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(Path.Combine(root, mainExe));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                    throw new FileNotFoundException("未找到产品主程序。", target);

                var paths = new List<string>();
                object rawEntries;
                if (config.TryGetValue("runtimePathEntries", out rawEntries))
                {
                    var entries = rawEntries as ArrayList;
                    if (entries != null)
                    {
                        foreach (object raw in entries)
                        {
                            string entry = Convert.ToString(raw ?? "").Trim().Replace('/', '\\');
                            if (string.Equals(entry, "{app}", StringComparison.OrdinalIgnoreCase)) entry = "";
                            else if (entry.StartsWith("{app}\\", StringComparison.OrdinalIgnoreCase)) entry = entry.Substring(6);
                            else continue;
                            if (entry.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                            string full = Path.GetFullPath(Path.Combine(root, entry));
                            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                                paths.Add(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        }
                    }
                }

                var start = new ProcessStartInfo();
                start.FileName = target;
                start.WorkingDirectory = Path.GetDirectoryName(target);
                start.UseShellExecute = false;
                start.Arguments = JoinArguments(args);
                string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                start.EnvironmentVariables["PATH"] = string.Join(";", paths.ToArray()) + (paths.Count > 0 && inheritedPath.Length > 0 ? ";" : "") + inheritedPath;

                using (Process child = Process.Start(start))
                {
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "无法启动程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static string GetString(Dictionary<string, object> config, string key)
        {
            object value;
            return config != null && config.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        // 遵循 CommandLineToArgvW 规则：仅在引号前加倍连续反斜杠，避免破坏路径尾部的反斜杠。
        static string JoinArguments(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var quoted = new List<string>();
            foreach (string arg in args) quoted.Add(QuoteArgument(arg ?? ""));
            return string.Join(" ", quoted.ToArray());
        }

        static string QuoteArgument(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return value;
            var result = new System.Text.StringBuilder("\"");
            int backslashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { backslashes++; continue; }
                if (ch == '"') result.Append('\\', backslashes * 2 + 1);
                else result.Append('\\', backslashes);
                result.Append(ch); backslashes = 0;
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}
