using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
            string deployedAppPath = GetArgumentValue(args, "--app");
            string sessionPath = GetArgumentValue(args, "--session");
            bool silent = args != null && args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            var form = new InstallerForm(deployedAppPath, sessionPath, silent);
            // 后置配置器只支持静默运行；保留 --ready-file 的兼容通知，不再保留旧交互/预览分支。
            WriteReadyFile(readyFile);
            form.SilentFinished += (s, e) => Application.ExitThread();
            form.BeginSilent();
            Application.Run();
        }

        static string GetReadyFilePath(string[] args)
        {
            return GetArgumentValue(args, "--ready-file");
        }

        static string GetArgumentValue(string[] args, string name)
        {
            if (args == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
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
        public long length;
    }

    class InstallerForm : Form
    {
        const string ConfigJson = @"__CONFIG_JSON__";

        Dictionary<string, object> cfg;
        string productName = "Application";
        string version = "1.0.0";
        string productId = "";
        string installPath = "";
        string mainExe = "";
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
        string controlPanelIcon = "";
        readonly string deployedAppPath;
        readonly string sessionPath;
        readonly bool silentMode;
        public event EventHandler SilentFinished;
        List<string> runtimePathEntries = new List<string>();
        List<OwnedFile> baseFileManifest = new List<OwnedFile>();
        List<CompInfo> components = new List<CompInfo>();
        string selectedPath = "";
        HashSet<string> selectedComps = new HashSet<string>();
        BackgroundWorker worker;

        public InstallerForm(string appPath, string sessionDirectory, bool silent)
        {
            deployedAppPath = NormalizeDeploymentPath(appPath);
            sessionPath = sessionDirectory ?? "";
            silentMode = silent;
            ParseConfig();
        }

        public void BeginSilent()
        {
            UpdateSessionState("configuring", "", 0, "正在初始化静默安装");
            if (string.IsNullOrEmpty(deployedAppPath))
            {
                UpdateSessionState("failed", "静默安装缺少已部署应用目录。", 100, "安装失败");
                if (SilentFinished != null) SilentFinished(this, EventArgs.Empty);
                return;
            }
            selectedPath = deployedAppPath;
            selectedComps.Clear();
            try
            {
                if (!string.IsNullOrEmpty(sessionPath) && File.Exists(sessionPath))
                {
                    var session = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(sessionPath)); object raw;
                    if (session != null && session.TryGetValue("selectedComponents", out raw)) { var chosen = raw as ArrayList; if (chosen != null) foreach (object item in chosen) selectedComps.Add(Convert.ToString(item)); }
                }
                foreach (CompInfo component in components) if (component.required) selectedComps.Add(component.name);
                BeginInstall();
            }
            catch (Exception ex)
            {
                UpdateSessionState("failed", ex.Message, 100, "安装失败");
                if (SilentFinished != null) SilentFinished(this, EventArgs.Empty);
            }
        }

        void ParseConfig()
        {
            try
            {
                cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ConfigJson);
                productName = GetStr(cfg, "productName", "Application");
                version = GetStr(cfg, "version", "1.0.0");
                productId = GetStr(cfg, "productId", GetStr(cfg, "upgradeCode", "")).Trim().Trim('{', '}').ToUpperInvariant();
                Guid parsedProductId; if (!Guid.TryParse(productId, out parsedProductId)) throw new InvalidDataException("产品唯一 ID 无效，已停止安装。");
                productId = parsedProductId.ToString("D").ToUpperInvariant();
                installPath = GetStr(cfg, "installPath", @"C:\Program Files\" + productName);
                mainExe = GetStr(cfg, "mainExe", "");
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
                controlPanelIcon = GetStr(cfg, "controlPanelIcon", "");
                runtimePathEntries = GetRuntimePathEntries(cfg);
                baseFileManifest = GetBaseFileManifest(cfg);
                selectedPath = !string.IsNullOrEmpty(deployedAppPath) ? deployedAppPath : installPath;
                object raw;
                if (cfg.TryGetValue("optionalComponents", out raw))
                {
                    var values = raw as ArrayList;
                    if (values != null) foreach (object item in values)
                    {
                        var d = item as Dictionary<string, object>; if (d == null) continue;
                        components.Add(new CompInfo { name=GetStr(d,"name",""), enabled=GetBool(d,"enabled",false), type=GetStr(d,"type",""), downloadUrl=GetStr(d,"downloadUrl",""), savePath=GetStr(d,"savePath",""), sizeBytes=GetLong(d,"sizeBytes",0), required=GetBool(d,"required",false) });
                    }
                }
            }
            catch (Exception ex) { throw new InvalidDataException("配置解析失败: " + ex.Message, ex); }
        }

        string GetStr(Dictionary<string, object> d, string key, string def) { object value; return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : def; }
        bool GetBool(Dictionary<string, object> d, string key, bool def) { object value; try { return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToBoolean(value) : def; } catch { return def; } }
        long GetLong(Dictionary<string, object> d, string key, long def) { object value; try { return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToInt64(value) : def; } catch { return def; } }
        string NormalizeDeploymentPath(string path) { if (string.IsNullOrWhiteSpace(path)) return ""; try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return ""; } }
        bool IsForeignProductDirectory(string path, out string owner)
        {
            owner = "";
            try { string manifest = Path.Combine(path, ".installer-uninstall.json"); if (!File.Exists(manifest)) return false; var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest)); owner = GetStr(record, "productName", "其他产品"); return !string.Equals(GetStr(record, "productId", "").Trim().Trim('{', '}'), productId, StringComparison.OrdinalIgnoreCase); }
            catch { owner = "未知产品"; return true; }
        }

        List<string> GetRuntimePathEntries(Dictionary<string, object> config)
        {
            var result = new List<string>(); object raw;
            if (config == null || !config.TryGetValue("runtimePathEntries", out raw)) return result;
            var values = raw as ArrayList; if (values == null) return result;
            foreach (object value in values) { string entry = Convert.ToString(value ?? "").Trim().Replace('/', '\\'); if (string.Equals(entry, "{app}", StringComparison.OrdinalIgnoreCase)) entry = "{app}"; else if (entry.StartsWith("{app}\\", StringComparison.OrdinalIgnoreCase) && entry.IndexOf("..", StringComparison.Ordinal) < 0) entry = "{app}\\" + entry.Substring(6).Trim('\\'); else continue; if (!result.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase))) result.Add(entry); }
            return result;
        }

        List<OwnedFile> GetBaseFileManifest(Dictionary<string, object> config)
        {
            var result = new List<OwnedFile>(); object raw;
            if (config == null || !config.TryGetValue("baseFileManifest", out raw)) return result;
            var values = raw as ArrayList; if (values == null) throw new InvalidDataException("基础文件清单格式无效。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in values) { var data = value as Dictionary<string, object>; if (data == null) throw new InvalidDataException("基础文件清单条目无效。"); string rel = GetStr(data,"relativePath","").Replace('/', '\\').TrimStart('\\'); if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.IndexOf("..",StringComparison.Ordinal)>=0 || !seen.Add(rel)) throw new InvalidDataException("基础文件清单路径无效：" + rel); result.Add(new OwnedFile { relativePath=rel, length=GetLong(data,"length",-1) }); }
            return result;
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
            if (!IsSafeInstallDirectory(selectedPath, out installError)) throw new InvalidDataException(installError);
            string foreignOwner;
            if (IsForeignProductDirectory(selectedPath, out foreignOwner)) throw new InvalidOperationException("该安装目录已属于“" + foreignOwner + "”，已拒绝覆盖。");
            worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_Completed;
            worker.RunWorkerAsync();
        }

        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            UpdateSessionState("configuring", "");
            var w = (BackgroundWorker)sender;
            var installedFiles = new List<OwnedFile>();
            try
            {
                w.ReportProgress(5, "正在确认主程序部署目录: " + selectedPath);
                if (!Directory.Exists(selectedPath))
                    throw new DirectoryNotFoundException("Inno 未完成主程序部署，找不到安装目录：" + selectedPath);

                // 基础程序已由 Inno 直接写入 {app}；后置配置器绝不再复制 source 文件。
                w.ReportProgress(20, "基础程序已直接部署，正在配置产品功能");

                // Shortcuts: mainExe is a relative path below the directory already deployed by Inno.
                string exeTarget = ResolveMainExeTarget();
                if ((createDesktop || createStartMenu || createStartup) && string.IsNullOrEmpty(exeTarget))
                    throw new FileNotFoundException("未找到主程序，无法创建快捷方式。请检查 mainExe 和安装包中的文件。", mainExe);
                string launchTarget = CreateRuntimeLauncherConfig(exeTarget);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = (createDesktop || createStartMenu) ? Activator.CreateInstance(shellType) : null;

                if (createStartMenu)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productName);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string lnk = Path.Combine(dir, productName + ".lnk");
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { launchTarget });
                    sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { startMenuArgs });
                    sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { selectedPath });
                    sc.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { ResolveShortcutIconLocation() });
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    w.ReportProgress(70, "创建开始菜单快捷方式");
                }
                if (createDesktop)
                {
                    string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), productName + ".lnk");
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { launchTarget });
                    sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { desktopArgs });
                    sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { selectedPath });
                    sc.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { ResolveShortcutIconLocation() });
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    w.ReportProgress(72, "创建桌面快捷方式");
                }

                // Current-user startup entry
                if (createStartup)
                {
                    string runName = string.IsNullOrEmpty(startupName) ? productName : startupName;
                    string runValue = "\"" + launchTarget + "\"" + (string.IsNullOrEmpty(startupArgs) ? "" : " " + startupArgs);
                    Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").SetValue(runName, runValue, RegistryValueKind.String);
                    w.ReportProgress(74, "创建启动项: " + runName);
                }

                // Download selected external resources to validated paths below the install directory.
                var toDownload = new List<CompInfo>();
                foreach (var name in selectedComps)
                    foreach (var c in components)
                        if (c.name == name && !string.IsNullOrEmpty(c.downloadUrl))
                            toDownload.Add(c);

                if (toDownload.Count > 0)
                {
                    int dlBase = 77, dlRange = 13;
                    for (int i = 0; i < toDownload.Count; i++)
                    {
                        var c = toDownload[i];
                        int componentStart = dlBase + (int)((float)i / toDownload.Count * dlRange);
                        int componentDone = dlBase + (int)((float)(i + 1) / toDownload.Count * dlRange);
                        w.ReportProgress(componentStart, "正在下载组件（" + (i + 1) + "/" + toDownload.Count + "）: " + c.name);
                        DownloadResource(c, w, componentStart, componentDone, i + 1, toDownload.Count);
                        UpdateDownloadSessionState("downloaded", c.name, i + 1, toDownload.Count, c.sizeBytes, c.sizeBytes, 0);
                        w.ReportProgress(componentDone, "组件已原样下载并部署: " + c.name);
                    }
                }

                // 基础程序采用构建期清单登记，避免安装完成后全目录枚举和哈希；仅对安装期间新增的已知文件补登记。
                w.ReportProgress(90, "正在登记构建期基础文件清单");
                installedFiles = CaptureBaseFileManifest();
                RegisterDynamicComponentFiles(installedFiles, toDownload);
                AddDynamicOwnedFile(installedFiles, Path.Combine(selectedPath, "runtime-launcher.exe"));
                AddDynamicOwnedFile(installedFiles, Path.Combine(selectedPath, "runtime-launcher.json"));
                if (!string.IsNullOrWhiteSpace(controlPanelIcon)) AddDynamicOwnedFile(installedFiles, Path.Combine(selectedPath, controlPanelIcon));
                WriteUninstallManifest(launchTarget, installedFiles);
                w.ReportProgress(100, "安装完成！");
            }
            catch (Exception ex)
            {
                e.Result = ex.Message;
                w.ReportProgress(100, "[错误] " + ex.Message);
            }
        }

        string ResolveShortcutIconLocation()
        {
            string productIcon = Path.Combine(selectedPath, "installer-product-icon.ico");
            return File.Exists(productIcon) ? productIcon + ",0" : Path.Combine(selectedPath, "runtime-launcher.exe") + ",0";
        }

        string CreateRuntimeLauncherConfig(string exeTarget)
        {
            string launcher = Path.Combine(selectedPath, "runtime-launcher.exe");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("缺少运行时 PATH 启动器。", launcher);

            string root = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relativeExe = Path.GetFullPath(exeTarget).Substring(root.Length).Replace('\\', '/');
            var safeEntries = new List<string>();
            foreach (string configuredEntry in runtimePathEntries)
            {
                string entry = configuredEntry ?? "";
                string relative;
                if (string.Equals(entry, "{app}", StringComparison.OrdinalIgnoreCase)) relative = "";
                else if (entry.StartsWith("{app}\\", StringComparison.OrdinalIgnoreCase)) relative = entry.Substring(6).Trim('\\');
                else throw new InvalidOperationException("运行时依赖目录必须以 {app} 为安装根：" + entry);
                if (relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException("运行时依赖目录超出安装目录：" + entry);
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("运行时依赖目录超出安装目录：" + entry);
                if (!Directory.Exists(full))
                    throw new DirectoryNotFoundException("运行时依赖目录不存在：" + entry);
                safeEntries.Add(string.IsNullOrEmpty(relative) ? "{app}" : "{app}\\" + relative.Replace('\\', '/'));
            }

            var launcherConfig = new Dictionary<string, object>();
            launcherConfig["mainExe"] = relativeExe;
            launcherConfig["runtimePathEntries"] = safeEntries;
            string configPath = Path.Combine(selectedPath, "runtime-launcher.json");
            File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(launcherConfig));
            return launcher;
        }

        List<OwnedFile> CaptureBaseFileManifest()
        {
            if (baseFileManifest == null || baseFileManifest.Count == 0) throw new InvalidDataException("构建期基础文件清单缺失，已拒绝进行不受控的全目录扫描。");
            string root = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var owned = new List<OwnedFile>();
            foreach (OwnedFile entry in baseFileManifest)
            {
                string path = Path.GetFullPath(Path.Combine(root, entry.relativePath));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new FileNotFoundException("基础文件与构建期清单不一致：" + entry.relativePath, path);
                if (entry.length >= 0 && new FileInfo(path).Length != entry.length) throw new InvalidDataException("基础文件长度与构建期清单不一致：" + entry.relativePath);
                owned.Add(new OwnedFile { relativePath = entry.relativePath, length = entry.length });
            }
            return owned;
        }
        void AddDynamicOwnedFile(List<OwnedFile> owned, string path)
        {
            if (owned == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string root = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path); if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("动态文件超出安装目录：" + path);
            string relative = full.Substring(root.Length).Replace('\\', '/');
            if (owned.Any(item => string.Equals(item.relativePath, relative, StringComparison.OrdinalIgnoreCase))) return;
            owned.Add(new OwnedFile { relativePath = relative, length = new FileInfo(full).Length });
        }
        void RegisterDynamicComponentFiles(List<OwnedFile> owned, List<CompInfo> downloaded)
        {
            foreach (CompInfo component in downloaded)
                AddDynamicOwnedFile(owned, SafeInstallFilePath(selectedPath, component.savePath));
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

        HttpWebResponse GetHttpsResponse(string value)
        {
            Uri uri; if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) throw new InvalidOperationException("组件下载 URL 无效。");
            for (int redirects = 0; redirects < 6; redirects++) { if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("组件下载 URL 或重定向地址必须是 HTTPS。"); HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect = false; request.Timeout = 30000; request.ReadWriteTimeout = 30000; HttpWebResponse response = (HttpWebResponse)request.GetResponse(); int code = (int)response.StatusCode; if (code >= 300 && code < 400) { string location = response.Headers[HttpResponseHeader.Location]; response.Close(); if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("HTTPS 重定向缺少目标地址。"); uri = new Uri(uri, location); continue; } if (!string.Equals(response.ResponseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) { response.Close(); throw new InvalidOperationException("组件响应不是 HTTPS。"); } return response; } throw new InvalidOperationException("HTTPS 重定向次数过多。");
        }
        void DownloadResource(CompInfo component, BackgroundWorker worker, int progressStart, int progressEnd, int componentIndex, int componentCount)
        {
            string type = string.IsNullOrWhiteSpace(component.type) ? "file" : component.type.Trim().ToLowerInvariant(); if (type != "file" && type != "zip" && type != "rar" && type != "tar.gz") throw new InvalidOperationException("不支持的组件类型：" + component.type + "。仅允许 file、zip、rar 或 tar.gz，且只会原样保存。");
            string destinationFile = SafeInstallFilePath(selectedPath, component.savePath); string targetDir = Path.GetDirectoryName(destinationFile); if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir); string downloadFile = Path.Combine(targetDir, "." + Path.GetFileName(destinationFile) + "." + Guid.NewGuid().ToString("N") + ".download"); if (File.Exists(destinationFile)) throw new InvalidOperationException("组件目标文件已存在；为避免覆盖，安装已停止：" + destinationFile);
            try
            {
                using (HttpWebResponse response = GetHttpsResponse(component.downloadUrl)) using (Stream input = response.GetResponseStream()) using (FileStream output = new FileStream(downloadFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long total = response.ContentLength > 0 ? response.ContentLength : component.sizeBytes;
                    long copied = 0; long speedBytes = 0; DateTime speedStarted = DateTime.UtcNow, lastSessionWrite = DateTime.MinValue;
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read); copied += read;
                        double elapsedSeconds = Math.Max(0.001, (DateTime.UtcNow - speedStarted).TotalSeconds); speedBytes = (long)(copied / elapsedSeconds);
                        DateTime now = DateTime.UtcNow;
                        if ((now - lastSessionWrite).TotalMilliseconds >= 250)
                        {
                            UpdateDownloadSessionState("downloading", component.name, componentIndex, componentCount, copied, total, speedBytes);
                            lastSessionWrite = now;
                        }
                        if (total > 0) worker.ReportProgress(progressStart + (int)((long)(progressEnd - progressStart) * Math.Min(total, copied) / total), "正在下载组件: " + component.name);
                    }
                    UpdateDownloadSessionState("downloading", component.name, componentIndex, componentCount, copied, total, speedBytes);
                }
            }
            catch (WebException ex) { throw new InvalidOperationException(DescribeDownloadError(component.downloadUrl, ex), ex); }
            File.Move(downloadFile, destinationFile); worker.ReportProgress(progressEnd, "组件已通过 HTTPS 下载并原样原子保存: " + component.name);
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

        static string SafeInstallFilePath(string installDirectory, string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath)) throw new InvalidOperationException("组件必须配置含文件名的 savePath。");
            return SafeInstallPath(installDirectory, savePath, true);
        }

        static string SafeInstallPath(string installDirectory, string relativePath, bool requireFileName)
        {
            string relative = (relativePath ?? "").Trim().Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.StartsWith("\\")) throw new InvalidOperationException("资源目标路径必须是相对安装目录的路径。");
            string[] parts = relative.Split('\\');
            if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == "..")) throw new InvalidOperationException("资源目标路径不允许包含 .、.. 或空路径段。");
            if (requireFileName && string.IsNullOrWhiteSpace(Path.GetFileName(relative))) throw new InvalidOperationException("savePath 必须包含文件名。");
            string root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("资源目标路径不能逃逸安装目录。");
            return target;
        }

        string ProductRegistryKey()
        {
            Guid parsed; if (!Guid.TryParse(productId, out parsed)) throw new InvalidOperationException("产品唯一 ID 无效，无法安全写入卸载信息。");
            return parsed.ToString("D").ToUpperInvariant();
        }
        void WriteUninstallManifest(string exeTarget, List<OwnedFile> installedFiles)
        {
            // Manifest 记录安装器创建的产品文件；卸载仅处理显式清单，绝不按目录递归删除。
            string uninstallExe = Path.Combine(selectedPath, productName + "-uninstall.exe");
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productName);
            string startupKey = string.IsNullOrEmpty(startupName) ? productName : startupName;
            File.Copy(Application.ExecutablePath, uninstallExe, true);
            installedFiles.Add(new OwnedFile { relativePath = Path.GetFileName(uninstallExe) });
            var manifest = new Dictionary<string, object>();
            manifest["schemaVersion"] = 4;
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
            manifest["cleanupDesktopShortcut"] = cleanupDesktop;
            manifest["cleanupStartMenuShortcut"] = cleanupStartMenu;
            manifest["cleanupStartupEntry"] = cleanupStartup;
            manifest["cleanupInstallDirectory"] = cleanupInstallDir;
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

        void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string msg = e.UserState as string ?? "正在配置安装";
            UpdateSessionState("configuring", "", Math.Min(100, e.ProgressPercentage), msg);
        }

        void UpdateSessionState(string state, string error) { UpdateSessionState(state, error, -1, ""); }
        void UpdateDownloadSessionState(string stage, string componentName, int componentIndex, int componentCount, long downloadedBytes, long totalBytes, long speedBytesPerSecond)
        {
            if (string.IsNullOrEmpty(sessionPath) || !File.Exists(sessionPath)) return;
            try
            {
                var serializer = new JavaScriptSerializer(); var session = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(sessionPath)); if (session == null) return;
                session["downloadStage"] = stage; session["componentName"] = componentName; session["componentIndex"] = componentIndex; session["componentCount"] = componentCount;
                session["downloadedBytes"] = downloadedBytes; session["totalBytes"] = totalBytes; session["speedBytesPerSecond"] = speedBytesPerSecond; session["updatedUtc"] = DateTime.UtcNow.ToString("o");
                string temporaryPath = sessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temporaryPath, serializer.Serialize(session)); if (File.Exists(sessionPath)) File.Delete(sessionPath); File.Move(temporaryPath, sessionPath);
            }
            catch { }
        }
        void UpdateSessionState(string state, string error, int progressValue, string message)
        {
            if (string.IsNullOrEmpty(sessionPath) || !File.Exists(sessionPath)) return;
            try
            {
                var serializer = new JavaScriptSerializer();
                var session = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(sessionPath));
                if (session == null) return;
                session["state"] = state;
                if (progressValue >= 0) session["progress"] = progressValue;
                if (!string.IsNullOrEmpty(message)) session["message"] = message;
                session["updatedUtc"] = DateTime.UtcNow.ToString("o");
                if (!string.IsNullOrEmpty(error)) session["error"] = error;
                string temporaryPath = sessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, serializer.Serialize(session));
                if (File.Exists(sessionPath)) File.Delete(sessionPath);
                File.Move(temporaryPath, sessionPath);
            }
            catch { }
        }

        void Worker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            bool succeeded = e.Error == null && !(e.Result is string);
            string error = succeeded ? "" : (e.Error != null ? e.Error.Message : Convert.ToString(e.Result));
            UpdateSessionState(succeeded ? "completed" : "failed", error, 100, succeeded ? "安装完成" : "安装失败");
            if (SilentFinished != null) SilentFinished(this, EventArgs.Empty);
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
            using (var pen = new Pen(Color.Transparent, 1))
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
                try { if (!string.Equals(Path.GetFullPath(path),Path.GetFullPath(Application.ExecutablePath),StringComparison.OrdinalIgnoreCase)) File.Delete(path); } catch { }
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
                var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
                string product = Str(cfg, "productName"), installPath = Str(cfg, "installPath"), productId = Str(cfg, "productId"), mainTarget = Str(cfg, "mainExeTarget");
                Guid parsed; if (!Guid.TryParse(productId, out parsed)) throw new InvalidDataException("卸载配置缺少有效的产品唯一 ID，已拒绝执行清理。");
                if (!string.Equals(Path.GetFullPath(installDir).TrimEnd('\\'), Path.GetFullPath(installPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("卸载程序路径与安装记录不一致，已拒绝执行清理。");
                if (MessageBox.Show("确定要卸载 " + product + " 吗？\r\n\r\n将仅删除安装清单中哈希仍匹配的产品文件，并撤销经归属校验的快捷方式、启动项和本产品卸载记录。\r\n任何被修改的文件、未知文件及非空目录都将保留。", "卸载确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs), startMenuDir = Path.Combine(programsDir, product);
                if (Bool(cfg, "cleanupDesktopShortcut")) { string p = Str(cfg, "desktopShortcut"); if (IsDirectChild(p, desktopDir, ".lnk") && IsOwnedShortcut(p, mainTarget, Str(cfg,"desktopArguments"))) File.Delete(p); }
                if (Bool(cfg, "cleanupStartMenuShortcut"))
                {
                    string p = Str(cfg, "startMenuShortcut");
                    if (IsDirectChild(p, startMenuDir, ".lnk") && IsOwnedShortcut(p, mainTarget, Str(cfg,"startMenuArguments"))) File.Delete(p);
                    // 只删除本产品目录且仅在快捷方式处理后确认其为空；不递归，不影响相邻产品目录。
                    try { if (Directory.Exists(startMenuDir) && Directory.GetFileSystemEntries(startMenuDir).Length == 0) Directory.Delete(startMenuDir, false); } catch { }
                }
                if (Bool(cfg, "cleanupStartupEntry")) { string n=Str(cfg,"startupEntryName"), expected=Str(cfg,"startupEntryValue"); using(RegistryKey run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)) { if(run!=null && string.Equals(Convert.ToString(run.GetValue(n,"")),expected,StringComparison.Ordinal)) run.DeleteValue(n,false); } }
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
        public bool enabled;
        public string type;
        public string downloadUrl;
        public string savePath;
        public long sizeBytes;
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
