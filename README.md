# 安装包制作工具（Installer Studio）

这是一个面向打包人员的 Windows 安装包制作工具。制作台采用原生 Windows Forms 窗口，不启动浏览器、不监听本地 HTTP 端口；生成的最终安装包也使用原生 Windows 界面。

## 功能

- 原生制作台：编辑产品、目录、快捷方式、环境变量和外部资源配置。
- 原生安装器：最终用户仅选择快速安装或自定义安装，不接触构建配置。
- 快速安装：安装内嵌的基础程序，不下载外部资源。
- 自定义安装：选择安装目录与需下载的可选资源。
- 单文件安装包：使用 Inno Setup 压缩基础程序，并生成带 UAC 提权的 `Setup.exe`。
- 实时构建日志：制作台直接显示 `build-worker.ps1` 的构建进度与输出路径。

## 目录结构

```text
installer/
├── .gitignore
├── README.md
├── test_source/                    # 示例基础程序目录
└── installer_exe/
    ├── installer-studio-native.ps1 # 原生制作台启动入口
    ├── installer-studio-native.cs  # 原生制作台源码
    ├── build-worker.ps1            # 实际构建工作进程
    ├── build-config.json           # 构建配置
    ├── installer-app.cs            # 最终用户安装器源码
    ├── launcher.cs                 # 单文件安装包启动器
    ├── app.manifest                # UAC 清单
    └── assets/icon.ico             # 默认图标
```

## 环境要求

- Windows 10/11
- PowerShell 5.1+
- .NET Framework 4.0+（用于编译和运行 WinForms 程序）
- Inno Setup 6.2+，默认安装位置为 `C:\Program Files (x86)\Inno Setup 6\`

## 使用制作台

在 `installer_exe` 目录中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer-studio-native.ps1
```

首次启动会编译 `installer-studio-native.cs`，随后直接打开 Windows 桌面窗口。制作台默认读取同目录的 `build-config.json`，可在窗口中导入、导出或保存配置。

### 配置项

| 类别 | 配置内容 |
|---|---|
| 产品信息 | 产品名称、版本、发布者、副标题、图标 |
| 基础程序 | 源程序目录、主 EXE、输出目录、默认安装目录 |
| 安装行为 | 桌面快捷方式、开始菜单快捷方式、环境变量、安装器主题 |
| 外部资源 | 资源名称、下载 URL、是否必选、解压路径、哈希值 |

点击“扫描基础程序目录”可核对待打包文件数量与体积。确认后点击“开始构建”，构建日志和生成文件位置会显示在制作台窗口中。

## 安装流程

```text
打包人员：原生制作台 → build-config.json → 构建 Setup.exe
最终用户：运行 Setup.exe → 选择快速安装或自定义安装 → 完成安装
```

最终用户安装器不会显示资源 URL、环境变量等构建期配置。

- **快速安装**：直接安装内嵌的基础运行环境，不下载外部资源。
- **自定义安装**：用户选择安装路径和需要的可选资源，再进行安装。

## 构建架构

```text
最终 Setup.exe
  └─ C# 启动器：请求管理员权限，提取内嵌负载
      └─ Inno Setup 负载：解压基础程序和安装器到临时目录
          └─ C# WinForms 安装器：展示最终用户安装界面
```

`build-worker.ps1` 会读取配置、编译 C# 安装器、复制基础程序、调用 Inno Setup 压缩负载，并将负载嵌入启动器，最终输出 `<产品名称>-Setup-<版本号>.exe`。

## 外部资源说明

配置中可为每项资源保留下载地址、解压目标和哈希值。当前已接入最终安装器构建链路的是资源名称、下载地址与必选状态；独立解压路径与 SHA-256 校验字段会随配置保存，尚待接入最终安装执行逻辑后生效。

建议将 1–2 GB 的资源制作为独立压缩包。基础程序随安装包使用 Inno Setup 的 LZMA2 压缩；外部资源应由服务器提供下载，并在后续版本中启用哈希校验、断点续传与安装根目录内的安全解压限制。

## 常见问题

### 找不到 ISCC.exe

安装 Inno Setup 6 到默认目录，或设置环境变量 `INNO_SETUP_PATH` 指向包含 `ISCC.exe` 的目录。

### 构建后文件名不是中文

Inno Setup 对编译阶段的输出名有限制。构建脚本会先使用安全名称完成编译，再重命名为最终产品名称；如果目标文件被占用，可能保留安全名称。

### 原生制作台无法启动

确认系统存在 `.NET Framework 4` 的 `csc.exe`，并使用上面的 `-NoProfile -ExecutionPolicy Bypass` 命令启动。
