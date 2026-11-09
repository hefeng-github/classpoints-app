# 发布脚本：把应用打成一个可以整目录拷走的文件夹
#
#   pwsh scripts/publish.ps1                    # 默认 win-x64
#   pwsh scripts/publish.ps1 -Runtime win-arm64 # 国产 ARM 一体机
#
# 不用 PublishSingleFile：WinUI 3 的 XBF 资源在单文件模式下要额外解包，
# 教室电脑上杀毒软件还容易拦自解压，整目录拷贝反而更省事、启动更快。

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 只要文件夹、不要 zip。教室那台机器是整目录拷 U 盘的，用不上压缩包。
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$outDir = Join-Path $root "dist/ClassPoints-$Runtime"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

Write-Host "==> 清理 $outDir" -ForegroundColor Cyan
# 用「挪走」而不是「删掉」：上一版发布目录可能正被运行中的 exe 占用，
# Windows 上强删会半途失败留下残缺目录，比不删更糟。挪走是原子的，
# 而且万一新版本有问题，上一版还在，挪回来就能用。
if (Test-Path $outDir) {
    $aside = Join-Path (Split-Path -Parent $outDir) "_previous-$stamp"
    Move-Item $outDir $aside
    Write-Host "    上一版已挪到 $aside（确认新版没问题后可以删）" -ForegroundColor DarkGray
}

Write-Host "==> 发布（$Configuration / $Platform / $Runtime）" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/ClassPoints.App/ClassPoints.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    -p:Platform=$platform `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=false `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit=$LASTEXITCODE）" }

# 发布目录里那九十来个语言文件夹（af-ZA / ar-SA …）是 WindowsAppSDK 自带的
# Microsoft.ui.xaml.dll.mui 资源，每个 40KB 左右，合计约 4MB —— 属于 WinUI 的正常组成，
# 删掉不是优化而是隐患（删漏一个就可能让某个控件加载资源失败）。
# .NET 卫星程序集那部分已经用 SatelliteResourceLanguages 在编译期裁到中英双语。

$exe = Join-Path $outDir 'ClassPoints.exe'
if (-not (Test-Path $exe)) { throw "没有产出 ClassPoints.exe，检查发布参数" }

# XAML 页面在编译期被做成 XBF 塞进 ClassPoints.pri，运行时不读 .xaml 源文件。
# 少了这个 pri，exe 双击起来就是 XamlParseException（MainWindow.InitializeComponent），
# 而且 `dotnet build` 的产物是好的、只有 publish 的产物坏，很容易看走眼。
# 根因是 csproj 里 EnableMsixTooling 被关掉时 MSIX 公共 targets 不导入，
# AddProjectPriToResolvedFileToPublish 也就不存在了。这里硬性卡一道。
foreach ($need in 'ClassPoints.pri', 'Microsoft.UI.Xaml.Controls.pri') {
    $p = Join-Path $outDir $need
    if (-not (Test-Path $p)) { throw "发布目录缺少 $need —— XAML 资源没进包，检查 csproj 的 EnableMsixTooling 是否仍为 true" }
}

# 一个启动脚本，双击就能用。
# 形态（大屏/电脑/只读）和预配置包都不走命令行参数了 —— 首次启动向导里问一次，
# 存在本机，之后启动直接进对应界面。理由：老师不会记参数，参数写死在快捷方式里
# 换台机器就静默进错形态。
@'
@echo off
start "" "%~dp0ClassPoints.exe"
'@ | Set-Content (Join-Path $outDir '启动.cmd') -Encoding Default

$size = (Get-ChildItem $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host "==> 完成：$outDir" -ForegroundColor Green
Write-Host ("    文件 {0} 个，共 {1:N1} MB" -f (Get-ChildItem $outDir -Recurse -File).Count, $size)
Write-Host "    整个目录拷到目标机，双击「启动.cmd」；第一次启动会让你选这台电脑做什么"

# 顺手压一个 zip：GitHub Actions 要把它传到 CNB 版本库当附件，
# 教室里也可能有人想发个压缩包给同事（邮件/网盘比整目录好传）。
#
# 用 Compress-Archive 而不是 ZipFile.CreateFromDirectory：后者要先 Add-Type
# 加载 System.IO.Compression.FileSystem，在受限环境里会被拦；Compress-Archive
# 是内置 cmdlet，哪都能跑。代价是慢一些（371 个文件约 45 秒），只在发布时发生。
if (-not $NoZip) {
    $zip = Join-Path $root "dist/ClassPoints-$Runtime.zip"
    if (Test-Path $zip) {
        # 同样是挪走不删：正在被别的进程读着的时候删不掉
        Move-Item $zip (Join-Path $root "dist/_previous-$stamp.zip")
    }

    # 进度条会把 CI 日志刷成几百行，而且没有信息量
    $prevProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Compress-Archive -Path $outDir -DestinationPath $zip -CompressionLevel Optimal
    }
    finally {
        $ProgressPreference = $prevProgress
    }

    $zipSize = (Get-Item $zip).Length / 1MB
    Write-Host ""
    Write-Host ("==> 压缩包：$zip（{0:N1} MB，解压出来是「{1}」文件夹）" -f $zipSize, (Split-Path -Leaf $outDir)) -ForegroundColor Green
}
