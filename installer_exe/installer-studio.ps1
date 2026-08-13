#Requires -Version 5.0
<#
.SYNOPSIS
    NewClass Installer Studio - Windows Installer Builder Tool
.DESCRIPTION
    A local web server that provides a browser-based UI for building
    Windows EXE installers using Inno Setup.
    Workflow: Select source dir -> Configure -> Build EXE
#>

$ErrorActionPreference = "Stop"

# Force UTF-8 encoding for console and output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# ========== Configuration ==========
$Port = 8424
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$InnoBinDir = if ($env:INNO_SETUP_PATH) { $env:INNO_SETUP_PATH } else { "C:\Program Files (x86)\Inno Setup 6" }
$HtmlFile = Join-Path $ScriptDir "installer-studio.html"
$Iscc = Join-Path $InnoBinDir "ISCC.exe"
$BuildConfigFile = Join-Path $ScriptDir "build-config.json"
$BuildStatusFile = Join-Path $ScriptDir "build-status.json"
$BuildWorkerScript = Join-Path $ScriptDir "build-worker.ps1"

# Load assemblies
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindow {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
'@
[System.Windows.Forms.Application]::EnableVisualStyles()

# ========== Utility Functions ==========

function Send-Json($response, $obj) {
    $json = $obj | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $response.ContentType = "application/json; charset=utf-8"
    $response.ContentLength64 = $bytes.Length
    $response.OutputStream.Write($bytes, 0, $bytes.Length)
    $response.OutputStream.Close()
}

function Send-Html($response, $path) {
    if (-not (Test-Path $path)) {
        $response.StatusCode = 404
        $response.OutputStream.Close()
        return
    }
    $content = [System.IO.File]::ReadAllBytes($path)
    $response.ContentType = "text/html; charset=utf-8"
    $response.ContentLength64 = $content.Length
    $response.OutputStream.Write($content, 0, $content.Length)
    $response.OutputStream.Close()
}

function Send-404($response) {
    $response.StatusCode = 404
    $body = [System.Text.Encoding]::UTF8.GetBytes("Not Found")
    $response.ContentLength64 = $body.Length
    $response.OutputStream.Write($body, 0, $body.Length)
    $response.OutputStream.Close()
}

function Read-RequestBody($request) {
    $reader = New-Object System.IO.StreamReader($request.InputStream, [System.Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd()
    $reader.Close()
    if ($body) { return $body | ConvertFrom-Json }
    return $null
}

# ========== API Handlers ==========

function Api-BrowseFolder() {
    # Use the Windows Explorer-style Open dialog shown in the requested reference.
    # The placeholder filename lets the current folder be confirmed with the Open button.
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Open"
    $dialog.Filter = "All Files (*.*)|*.*"
    $dialog.CheckFileExists = $false
    $dialog.CheckPathExists = $true
    $dialog.ValidateNames = $false
    $dialog.FileName = "Select this folder"
    $dialog.Multiselect = $false
    $dialog.RestoreDirectory = $true
    $dialog.AddExtension = $false

    $owner = $null
    $foregroundHandle = [NativeWindow]::GetForegroundWindow()
    if ($foregroundHandle -ne [IntPtr]::Zero) {
        $owner = New-Object System.Windows.Forms.NativeWindow
        $owner.AssignHandle($foregroundHandle)
    }

    try {
        $result = if ($owner) { $dialog.ShowDialog($owner) } else { $dialog.ShowDialog() }
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            $selectedPath = [System.IO.Path]::GetDirectoryName($dialog.FileName)
            if ($selectedPath -and (Test-Path -LiteralPath $selectedPath -PathType Container)) {
                return @{ success = $true; path = $selectedPath }
            }
        }
        return @{ success = $false; path = "" }
    } finally {
        if ($owner) { $owner.ReleaseHandle() }
        $dialog.Dispose()
    }
}

function Api-BrowseFile($filter, $title) {
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = if ($title) { $title } else { "Select File" }
    if ($filter) {
        $dialog.Filter = $filter
    } else {
        $dialog.Filter = "All Files (*.*)|*.*"
    }
    $dialog.CheckFileExists = $true
    $dialog.CheckPathExists = $true
    $dialog.Multiselect = $false
    $dialog.RestoreDirectory = $true

    # Use the same NativeWindow approach as Api-BrowseFolder for reliability
    $owner = $null
    $foregroundHandle = [NativeWindow]::GetForegroundWindow()
    if ($foregroundHandle -ne [IntPtr]::Zero) {
        $owner = New-Object System.Windows.Forms.NativeWindow
        $owner.AssignHandle($foregroundHandle)
    }

    try {
        $result = if ($owner) { $dialog.ShowDialog($owner) } else { $dialog.ShowDialog() }
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            return @{ success = $true; path = $dialog.FileName }
        }
        return @{ success = $false; path = "" }
    } finally {
        if ($owner) { $owner.ReleaseHandle() }
        $dialog.Dispose()
    }
}

function Api-ScanDir($path) {
    if (-not (Test-Path $path)) {
        return @{ success = $false; error = "Directory not found"; fileCount = 0; totalSizeMB = 0 }
    }
    try {
        $files = Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue
        $count = ($files | Measure-Object).Count
        $sizeBytes = ($files | Measure-Object -Property Length -Sum).Sum
        if (-not $sizeBytes) { $sizeBytes = 0 }
        $sizeMB = [math]::Round($sizeBytes / 1MB, 2)

        $topItems = Get-ChildItem -Path $path -ErrorAction SilentlyContinue | Select-Object -First 20 | ForEach-Object {
            @{
                name = $_.Name
                type = if ($_.PSIsContainer) { "dir" } else { "file" }
                sizeKB = if ($_.PSIsContainer) { 0 } else { [math]::Round($_.Length / 1KB, 1) }
            }
        }

        return @{
            success = $true
            fileCount = $count
            totalSizeMB = $sizeMB
            totalSizeGB = [math]::Round($sizeBytes / 1GB, 2)
            items = $topItems
        }
    } catch {
        return @{ success = $false; error = $_.Exception.Message; fileCount = 0; totalSizeMB = 0 }
    }
}

function Api-WixInfo() {
    # Endpoint name is retained for existing clients; its payload now reports Inno Setup.
    $isccExists = Test-Path $Iscc
    return @{
        available = $isccExists
        wixPath = $InnoBinDir
        candlePath = $Iscc
        lightPath = $Iscc
        candleExists = $isccExists
        lightExists = $isccExists
        engine = 'Inno Setup'
        compilerPath = $Iscc
    }
}

# ========== Build Engine ==========

function Api-Build($config) {
    # Check if build is already running
    $currentStatus = Api-BuildStatus
    if ($currentStatus.status -eq 'running' -or $currentStatus.status -eq 'starting') {
        return @{ success = $false; error = 'A build is already in progress' }
    }

    # Check build worker exists
    if (-not (Test-Path $BuildWorkerScript)) {
        return @{ success = $false; error = 'Build worker script not found: ' + $BuildWorkerScript }
    }

    # Save config to file
    $configJson = $config | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($BuildConfigFile, $configJson, (New-Object Text.UTF8Encoding $false))

    # Initialize status file
    $initStatus = @{
        status = 'starting'
        progress = 0
        log = @('Starting build process...')
        output = ''
        error = ''
        timestamp = (Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($BuildStatusFile, $initStatus, (New-Object Text.UTF8Encoding $false))

    # Start build worker in a separate hidden process
    $psExe = Join-Path $PSHOME "powershell.exe"
    if (-not (Test-Path $psExe)) { $psExe = "powershell.exe" }

    Start-Process -FilePath $psExe -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$BuildWorkerScript`"",
        "-ConfigFile", "`"$BuildConfigFile`"",
        "-StatusFile", "`"$BuildStatusFile`"",
        "-InnoBinDir", "`"$InnoBinDir`"",
        "-ScriptDir", "`"$ScriptDir`""
    ) -WindowStyle Hidden

    return @{ success = $true; message = 'Build started' }
}

function Api-BuildStatus() {
    if (Test-Path $BuildStatusFile) {
        try {
            $content = [IO.File]::ReadAllText($BuildStatusFile, [Text.Encoding]::UTF8)
            if ($content) {
                $status = $content | ConvertFrom-Json
                # Convert log to array if it's not already
                $logArray = @()
                if ($status.log) {
                    $logArray = @($status.log)
                }
                return @{
                    status = $status.status
                    progress = $status.progress
                    log = $logArray
                    output = $status.output
                    error = $status.error
                }
            }
        } catch {
            return @{ status = 'idle'; progress = 0; log = @(); output = ''; error = '' }
        }
    }
    return @{ status = 'idle'; progress = 0; log = @(); output = ''; error = '' }
}

# ========== HTTP Server ==========

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  NewClass Installer Studio" -ForegroundColor Cyan
Write-Host "  Local Server: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Check Inno Setup
$innoAvailable = Test-Path $Iscc
if (-not $innoAvailable) {
    Write-Host "[WARNING] Inno Setup ISCC.exe not found at: $InnoBinDir" -ForegroundColor Yellow
    Write-Host "  Install Inno Setup 6/7 or set INNO_SETUP_PATH." -ForegroundColor Yellow
} else {
    Write-Host "[OK] Inno Setup found: $InnoBinDir" -ForegroundColor Green
}

# Check HTML file
if (-not (Test-Path $HtmlFile)) {
    Write-Host "[ERROR] UI file not found: $HtmlFile" -ForegroundColor Red
    Write-Host "  Please ensure installer-studio.html exists." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] UI file found: $HtmlFile" -ForegroundColor Green

# Check build worker
if (-not (Test-Path $BuildWorkerScript)) {
    Write-Host "[WARNING] Build worker not found: $BuildWorkerScript" -ForegroundColor Yellow
} else {
    Write-Host "[OK] Build worker found: $BuildWorkerScript" -ForegroundColor Green
}

# Start listener
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
try {
    $listener.Start()
} catch {
    Write-Host "[ERROR] Failed to start server on port $Port" -ForegroundColor Red
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Try a different port or check if the port is in use." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Server started. Opening browser..." -ForegroundColor Green

# Open browser
Start-Process "http://localhost:$Port/"

Write-Host ""
Write-Host "Press Ctrl+C to stop the server." -ForegroundColor Gray
Write-Host ""

# Main request loop
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        $path = $request.Url.LocalPath
        $method = $request.HttpMethod

        try {
            switch -Wildcard ("$method $path") {
                "GET /" {
                    Send-Html $response $HtmlFile
                }
                "GET /api/wix-info" {
                    Send-Json $response (Api-WixInfo)
                }
                "POST /api/browse-folder" {
                    $result = Api-BrowseFolder
                    Send-Json $response $result
                }
                "POST /api/browse-file" {
                    $body = Read-RequestBody $request
                    $result = Api-BrowseFile $body.filter $body.title
                    Send-Json $response $result
                }
                "GET /api/scan-dir*" {
                    $queryParams = [System.Web.HttpUtility]::ParseQueryString($request.Url.Query)
                    $dirPath = $queryParams["path"]
                    $result = Api-ScanDir $dirPath
                    Send-Json $response $result
                }
                "POST /api/build" {
                    $body = Read-RequestBody $request
                    $result = Api-Build $body
                    Send-Json $response $result
                }
                "GET /api/build/status" {
                    $result = Api-BuildStatus
                    Send-Json $response $result
                }
                default {
                    Send-404 $response
                }
            }
        } catch {
            try {
                $errorObj = @{ success = $false; error = $_.Exception.Message }
                Send-Json $response $errorObj
            } catch {
                $response.StatusCode = 500
                $response.OutputStream.Close()
            }
        }
    }
} finally {
    if ($listener) {
        $listener.Stop()
        $listener.Close()
    }
    Write-Host ""
    Write-Host "Server stopped." -ForegroundColor Yellow
}
