# 用途：按明确的允许清单生成全新的公开源码副本，原项目和个人知识文件保持在原位置。
param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destinationRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationRoot) { throw '目标已存在，请指定新的公开副本目录，避免与旧文件混合。' }
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

# 只复制这些工程源文件类型；bin/obj/测试结果/运行数据无论在哪里都不能进入副本。
$extensions = @('.cs','.csproj','.axaml','.json','.props','.targets','.ps1','.yml','.yaml')
foreach ($folder in @('src','tests','scripts','.github')) {
    $source = Join-Path $projectRoot $folder
    if (!(Test-Path -LiteralPath $source)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj|AvaloniaUI|TestResults|local-data|data|logs)([\\/]|$)' -or
            $file.Name -in @('appsettings.Workspace.json','appsettings.Local.json','settings.json') -or
            $file.Extension -notin $extensions) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '源文件不允许是链接。' }
        $target = Join-Path $destinationRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
foreach ($name in @('.gitignore','AGENTS.md','Directory.Build.props','global.json','PrivateKnowledge.sln','PrivateKnowledge.Client.sln','PrivateKnowledge.slnLaunch','LICENSE','start-client.ps1','start-service.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $destinationRoot $name)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'PublicSamples') -Destination (Join-Path $destinationRoot 'PublicSamples') -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $destinationRoot 'licenses') -Recurse
# 公开文档单独维护，不把历史排障记录、个人档案或本机验收日志当成项目文档上传。
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs/public')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/public') -Destination (Join-Path $destinationRoot 'docs') -Recurse
    Move-Item -LiteralPath (Join-Path $destinationRoot 'docs/README.md') -Destination (Join-Path $destinationRoot 'README.md')
} else {
    # 从已经导出的公开仓库再次打包时，文档已位于 docs/，README 已位于根目录。
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination (Join-Path $destinationRoot 'docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $destinationRoot 'README.md')
}
# 自动测试引用的是已核对过的固定中文 PDF，而不是用户上传的 PDF。
$fixture = 'checks/PrivateKnowledge.FormatChecks/Samples/中文操作指南.pdf'
New-Item -ItemType Directory -Path (Split-Path (Join-Path $destinationRoot $fixture) -Parent) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot $fixture) -Destination (Join-Path $destinationRoot $fixture)
# 可选的真实模型验收工程明确列出，避免复制旧演示和任何真实资料处理脚本。
foreach ($folder in @('PrivateKnowledge.SemanticChecks','PrivateKnowledge.FormatChecks')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot ('checks/' + $folder)) -File) {
        if ($file.Extension -notin @('.cs','.csproj')) { continue }
        $target = Join-Path $destinationRoot ('checks/' + $folder + '/' + $file.Name)
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$manualCheck = 'checks/PrivateKnowledge.DesktopChecks'
New-Item -ItemType Directory -Path (Join-Path $destinationRoot $manualCheck) -Force | Out-Null
foreach ($name in @('PrivateKnowledge.DesktopChecks.csproj','CheckAppBuilder.cs','FirstRunReleaseCheck.cs')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ($manualCheck + '/' + $name)) -Destination (Join-Path $destinationRoot ($manualCheck + '/' + $name))
}
& (Join-Path $PSScriptRoot 'assert-public.ps1') -Path $destinationRoot -Kind source
Write-Host ('PUBLIC_SOURCE=' + $destinationRoot)
