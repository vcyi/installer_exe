# 安装包制作工具 (Installer Studio)

一个基于 PowerShell + C# + Inno Setup 的 Windows 安装包制作工具，支持可视化配置、原生 EXE 安装界面、快速安装/自定义安装模式。

## 功能特性

- **可视化配置界面**：通过浏览器网页配置安装包参数
- **原生 C# 安装界面**：不依赖 Web/HTML，纯 Windows Forms 原生 EXE
- **快速安装**：一键安装基础运行环境，使用默认设置
- **自定义安装**：可选择安装路径和可选组件（支持在线下载）
- **中文完美支持**：文件名、界面文字、日志均无乱码
- **UAC 提权**：自动请求管理员权限安装到 Program Files
- **快捷方式 & 环境变量**：自动创建桌面/开始菜单快捷方式，设置环境变量

## 目录结构

```
installer/
├── .gitignore
├── README.md
└── installer_exe/
    ├── installer-studio.ps1      # 后端 API 服务器（端口 8424）
    ├── installer-studio.html      # 前端配置界面
    ├── build-worker.ps1           # 构建脚本（编译 + 打包）
    ├── installer-app.cs           # C# 原生安装器源码
    ├── launcher.cs                # C# 启动器源码（UAC + 静默调度）
    ├── app.manifest               # 启动器清单文件（requireAdministrator）
    ├── build-config.json          # 构建配置示例
    ├── assets/
    │   └── icon.ico               # 默认图标
    └── test_source/               # 测试用源文件
        ├── TestApp.exe
        ├── config.ini
        └── lib/
            └── library.dll
```

## 环境要求

- **操作系统**：Windows 10/11 (x64)
- **PowerShell**：5.1+（系统自带）
- **Inno Setup**：6.2+（需安装到默认路径 `C:\Program Files (x86)\Inno Setup 6\`）
- **.NET Framework**：4.0+（系统自带，用于编译 C# 程序）

## 快速开始

### 1. 启动安装包制作工具

以管理员身份运行 PowerShell，执行：

```powershell
cd C:\Users\10101\Documents\workpath\installer\installer_exe
.\installer-studio.ps1
```

浏览器访问 `http://localhost:8424/` 即可打开配置界面。

### 2. 配置安装包参数

在网页界面中填写：

| 参数 | 说明 | 示例 |
|------|------|------|
| 产品名称 | 安装包显示的名称 | 智慧教学系统 |
| 版本号 | 产品版本 | 3.2.1 |
| 发布者 | 公司名称 | 教育科技公司 |
| 源文件目录 | 要打包的程序文件目录 | C:\path\to\app |
| 安装路径 | 默认安装目录 | C:\Program Files\xxx |
| 主程序 | 安装后运行的主 EXE | app.exe |
| 图标 | 安装包图标 (.ico) | 可选 |
| 桌面快捷方式 | 是否创建桌面快捷方式 | 勾选 |
| 开始菜单快捷方式 | 是否创建开始菜单快捷方式 | 勾选 |
| 环境变量 | 需要设置的变量名和值 | SMART_TEACH_HOME = {app}\bin |
| 可选组件 | 组件名称及下载地址 | 题库包、素材库等 |

### 3. 构建安装包

点击"开始构建"按钮，等待构建完成。构建日志会实时显示在网页上。

### 4. 获取安装包

构建完成后，安装包生成在 `installer_exe/` 目录下，文件名格式为：

```
<产品名称>-Setup-<版本号>.exe
```

例如：`智慧教学系统-Setup-3.2.1.exe`

## 安装包架构

生成的安装包采用三层架构，确保用户体验流畅：

```
用户双击 EXE
    │
    ▼
┌─────────────────────────────────┐
│  C# 启动器 (launcher.exe)       │  ← UAC 提权
│  - 内嵌 Inno Setup 负载          │
│  - 提取并以 /VERYSILENT 模式运行 │
└──────────────┬──────────────────┘
               │
               ▼
┌─────────────────────────────────┐
│  Inno Setup (setup-payload.exe) │  ← 静默运行，无界面
│  - 解压文件到临时目录            │
│  - 启动 C# 安装器                │
└──────────────┬──────────────────┘
               │
               ▼
┌─────────────────────────────────┐
│  C# 安装器 (installer-app.exe)  │  ← 用户可见界面
│  - 欢迎页：快速安装 / 自定义安装  │
│  - 自定义：选择路径和组件         │
│  - 安装进度：复制文件、创建快捷方式│
│  - 完成：安装成功提示             │
└─────────────────────────────────┘
```

### 为什么用三层架构？

1. **C# 启动器**：提供 UAC 提权（通过 manifest），内嵌整个安装包为单个 EXE
2. **Inno Setup**：负责文件压缩和解压（LZMA2），以 `/VERYSILENT` 模式运行，用户不可见
3. **C# 安装器**：原生 Windows Forms 界面，不依赖浏览器或 HTTP 服务器，可在任何 Windows 电脑上运行

## 构建流程详解

`build-worker.ps1` 按以下步骤构建安装包：

1. **解析配置** — 读取 build-config.json
2. **编译 C# 安装器** — 将配置嵌入 installer-app.cs，用 csc.exe 编译为 installer-app.exe
3. **准备源文件** — 将源文件目录复制到构建临时目录
4. **生成 Inno Setup 脚本** — 动态生成 .iss 文件，配置文件列表和安装逻辑
5. **编译 Inno Setup** — 用 ISCC.exe 编译为 setup-payload.exe
6. **编译 C# 启动器** — 将 setup-payload.exe 作为资源嵌入 launcher.cs，生成最终 EXE
7. **清理** — 删除中间文件 setup-payload.exe

## 在其他电脑上使用安装包

生成的 `*-Setup-*.exe` 是完全独立的单个文件，可以：

- 直接拷贝到其他 Windows 电脑运行
- 不需要安装 PowerShell、Inno Setup 或 .NET SDK
- 仅依赖目标电脑自带的 .NET Framework 4.0+
- 双击运行 → UAC 确认 → 选择安装方式 → 完成

## 命令行工具

### 直接触发构建（不通过网页界面）

```powershell
# 读取配置并以 UTF-8 编码发送到 API
$configJson = [IO.File]::ReadAllText('build-config.json', [Text.Encoding]::UTF8)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($configJson)
Invoke-RestMethod -Uri 'http://localhost:8424/api/build' -Method Post -Body $bytes -ContentType 'application/json; charset=utf-8'

# 查询构建状态
Invoke-RestMethod -Uri 'http://localhost:8424/api/build/status' -Method Get
```

## 常见问题

### Q: 构建时报 "ISCC.exe not found"

安装 Inno Setup 6 到默认路径：`C:\Program Files (x86)\Inno Setup 6\`

### Q: 生成的安装包文件名是乱码或下划线

Inno Setup 的 ISCC 不支持非 ASCII 的 OutputBaseFilename。构建脚本会先用 ASCII 名称编译，再重命名为中文名。如果目标文件被占用无法重命名，会保留 ASCII 名称。

### Q: 安装包运行时没有显示界面

确保目标电脑有 .NET Framework 4.0+（Windows 10 自带）。检查任务管理器中是否有 `installer-app.exe` 进程。

### Q: PowerShell 发送中文参数变为问号

PowerShell 5 的 `Invoke-RestMethod` 默认不使用 UTF-8 编码。需要将 body 转为字节数组发送：

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
Invoke-RestMethod -Uri '...' -Method Post -Body $bytes -ContentType 'application/json; charset=utf-8'
```

## 技术栈

| 组件 | 技术 |
|------|------|
| 配置界面 | HTML + CSS + JavaScript |
| API 服务器 | PowerShell HttpListener |
| 构建脚本 | PowerShell 5.1 |
| 安装器界面 | C# Windows Forms (.NET Framework 4.0) |
| 文件打包 | Inno Setup 6 (LZMA2 压缩) |
| 编译器 | csc.exe (C# 5.0 兼容) |
