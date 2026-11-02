# 用途：从干净公开源码构建 Windows x64 自带运行时包，并输出源码 ZIP、运行 ZIP 和 SHA256。
# 不依赖 Python，不安装 Ollama，不下载模型，不删除旧发布或用户资料。
param([string]$Version = '0.1.1', [string]$OutputRoot = '', [string]$RuntimeVersion = '9.0.22')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw '版本号格式不正确。' }
if ($RuntimeVersion -notmatch '^9\.0\.\d+$') { throw '本工程的运行时补丁必须为 9.0.x。' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'release' }
$releaseRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('v' + $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $releaseRoot) { throw '发布目录已存在，请稍后重试。' }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$cleanSource = Join-Path $releaseRoot 'source/OfflinePrivateKnowledge'
& (Join-Path $PSScriptRoot 'export-source.ps1') -Destination $cleanSource
$sourceZip = Join-Path $releaseRoot ('offline-private-knowledge-v' + $Version + '-source.zip')
Compress-Archive -LiteralPath $cleanSource -DestinationPath $sourceZip -CompressionLevel Optimal
# 构建副本允许产生 obj/bin；公开源码目录始终保持干净，避免把 NuGet 缓存和本机路径装进源码包。
$buildRoot = Join-Path $releaseRoot 'build/OfflinePrivateKnowledge'
New-Item -ItemType Directory -Path (Split-Path $buildRoot -Parent) -Force | Out-Null
Copy-Item -LiteralPath $cleanSource -Destination $buildRoot -Recurse
$desktop = Join-Path $buildRoot 'src/PrivateKnowledge.Desktop/PrivateKnowledge.Desktop.csproj'
$runtime = Join-Path $releaseRoot 'windows/OfflinePrivateKnowledge'
# 保留现有开发 SDK；发布时单独固定 .NET 9 的运行时补丁，避免把 SDK 自带的旧补丁装进用户包。
& dotnet restore $desktop -r win-x64 -p:SelfContained=true -p:RuntimeFrameworkVersion=$RuntimeVersion
if ($LASTEXITCODE -ne 0) { throw 'Windows 运行时依赖还原失败。' }
& dotnet publish $desktop -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:RuntimeFrameworkVersion=$RuntimeVersion -p:PublishSingleFile=false -o $runtime
if ($LASTEXITCODE -ne 0) { throw 'Windows 自带运行时发布失败。' }
# 发布目录是本次全新创建的绝对目录。只删除本次目录中的编译符号文件，不做目录递归删除。
$allowed = [IO.Path]::GetFullPath($runtime) + [IO.Path]::DirectorySeparatorChar
foreach ($symbol in Get-ChildItem -LiteralPath $runtime -Recurse -File -Filter '*.pdb') {
    if (!$symbol.FullName.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝处理发布目录外的文件。' }
    Remove-Item -LiteralPath $symbol.FullName
}
Copy-Item -LiteralPath (Join-Path $cleanSource 'LICENSE') -Destination $runtime
Copy-Item -LiteralPath (Join-Path $cleanSource 'docs/QUICKSTART.md') -Destination (Join-Path $runtime '开始使用.md')
& (Join-Path $PSScriptRoot 'export-notices.ps1') -BuildRoot $buildRoot -Destination (Join-Path $runtime 'licenses')
& (Join-Path $PSScriptRoot 'assert-public.ps1') -Path $runtime -Kind runtime -SampleRoot (Join-Path $cleanSource 'PublicSamples')
$runtimeZip = Join-Path $releaseRoot ('offline-private-knowledge-v' + $Version + '-win-x64.zip')
Compress-Archive -LiteralPath $runtime -DestinationPath $runtimeZip -CompressionLevel Optimal
$checksums = @($sourceZip, $runtimeZip) | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
$checksums | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding UTF8
[ordered]@{version=$Version;sourceArchive=[IO.Path]::GetFileName($sourceZip);windowsArchive=[IO.Path]::GetFileName($runtimeZip);runtime=('win-x64 self-contained ' + $RuntimeVersion);publicAudit='PASS'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-info.json') -Encoding UTF8
Write-Host ('RELEASE_ROOT=' + $releaseRoot)
