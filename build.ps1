[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_CERT_REVOCATION_MODE = 'offline'

function Invoke-OfflineDotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "离线 dotnet 命令失败（退出码 $LASTEXITCODE）。" }
}

function Test-LocalOnlySource {
    # 简单防回归检查；不能替代原生库审计或操作系统级全流量审计。
    $sourceFiles = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Extension -in @('.cs', '.xaml', '.csproj') }
    $forbidden = $sourceFiles | Select-String -Pattern 'HttpClient|WebClient|WebRequest|System\.Net\.|Process\.Start|ProcessStartInfo|WebView|DllImport\("(?:wininet|winhttp|urlmon)'
    if ($forbidden) { throw '应用源码出现网络客户端、外部进程启动或网页控件，请先核查；未发布。' }
    Write-Output '应用源码本地处理规则检查通过。'
}

function Copy-LocalRuntime {
    param([string]$Destination)
    # 从构建机已安装的微软 x64 运行库取本地副本，不恢复/下载运行时 NuGet 包。
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -CommandType Application).Source
    $desktopRoot = Join-Path $dotnetRoot 'shared/Microsoft.WindowsDesktop.App'
    $runtimeVersion = Get-ChildItem -LiteralPath $desktopRoot -Directory |
        Where-Object { $_.Name -match '^10\.0\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending |
        Where-Object { (Test-Path -LiteralPath (Join-Path $dotnetRoot "shared/Microsoft.NETCore.App/$($_.Name)")) -and
            (Test-Path -LiteralPath (Join-Path $dotnetRoot "host/fxr/$($_.Name)/hostfxr.dll")) } |
        Select-Object -First 1 -ExpandProperty Name
    if (-not $runtimeVersion) { throw '没有可供离线打包的匹配 .NET 10 x64 核心及桌面运行库。不会下载。' }
    $runtimeDestination = Join-Path $Destination 'runtime'
    $runtimePaths = @("shared/Microsoft.NETCore.App/$runtimeVersion", "shared/Microsoft.WindowsDesktop.App/$runtimeVersion", "host/fxr/$runtimeVersion")
    foreach ($relativePath in $runtimePaths) {
        $target = Join-Path $runtimeDestination $relativePath
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $dotnetRoot $relativePath) | Copy-Item -Destination $target -Recurse -Force
    }
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $name) -Destination (Join-Path $runtimeDestination $name)
    }
    "随附 Microsoft .NET 核心运行库及 Windows Desktop Runtime $runtimeVersion (x64)，来源为本机构建环境。许可见 runtime/LICENSE.txt 和 runtime/ThirdPartyNotices.txt。" |
        Set-Content -LiteralPath (Join-Path $Destination '运行库说明.txt') -Encoding utf8
}

function Publish-LocalApp {
    param([string]$Project, [string]$OutputName, [string]$Executable, [string]$Readme = '使用说明.txt')
    $output = "dist/$OutputName"
    Invoke-OfflineDotnet -Arguments @('publish', $Project, '--configuration', 'Release', '--no-restore', '--self-contained', 'false', '-p:BundleLocalRuntime=true', '--output', $output, '--nologo')
    Copy-LocalRuntime -Destination (Join-Path $PSScriptRoot $output)
    Copy-Item -LiteralPath $Readme -Destination "$output/$Readme"
    Copy-Item -LiteralPath 'vendor/THIRD-PARTY-NOTICES.txt' -Destination "$output/THIRD-PARTY-NOTICES.txt"
    $launcher = "@echo off`r`nsetlocal`r`nstart `"`" `"%~dp0$Executable`"`r`n"
    Set-Content -LiteralPath "$output/启动工具.cmd" -Value $launcher -Encoding ascii
    # 发布文件夹自身做一次只读启动检查；检查只使用内存中的合成内容，不读取用户文件。
    $verify = Start-Process -FilePath (Join-Path (Resolve-Path $output) $Executable) -ArgumentList '--verify-local-startup' -PassThru -WindowStyle Hidden
    try {
        if (-not $verify.WaitForExit(30000)) { throw "发布文件夹启动检查超过 30 秒，未确认可用：$OutputName。" }
        if ($verify.ExitCode -ne 0) { throw "发布文件夹启动检查失败：$OutputName。" }
    }
    finally { $verify.Dispose() }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    Test-LocalOnlySource
    Invoke-OfflineDotnet -Arguments @('restore', 'CRTVUP.Unify.LocalPdf.sln', '--configfile', 'NuGet.Config', '--nologo')
    Invoke-OfflineDotnet -Arguments @('build', 'CRTVUP.Unify.LocalPdf.sln', '--configuration', 'Release', '--no-restore', '--nologo')
    if (-not $SkipTests) {
        Invoke-OfflineDotnet -Arguments @('run', '--project', 'tests/CRTVUP.Unify.LocalPdf.Tests', '--configuration', 'Release', '--no-build', '--no-restore')
        $verificationDirectory = Join-Path $PSScriptRoot ('artifacts/verification-' + [Guid]::NewGuid().ToString('N'))
        Invoke-OfflineDotnet -Arguments @('run', '--project', 'tests/CRTVUP.Unify.LocalPdf.UiTests', '--configuration', 'Release', '--no-build', '--no-restore', '--', $verificationDirectory)
    }
    Publish-LocalApp -Project 'src/CRTVUP.Unify.LocalPdf.Desktop' -OutputName 'LocalPdf' -Executable 'LocalPdf.exe'
    Publish-LocalApp -Project 'src/CRTVUP.Unify.LocalPdf.DesktopV2' -OutputName 'LocalPdfV2' -Executable 'LocalPdfV2.exe' -Readme '使用说明-工具箱.txt'
    Write-Output '构建与检查完成。V1：dist\LocalPdf\LocalPdf.exe；V2 工具箱：dist\LocalPdfV2\LocalPdfV2.exe。'
}
finally { Pop-Location }
