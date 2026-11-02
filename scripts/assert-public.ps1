# 用途：检查待公开副本，拒绝运行数据、个人目录、常见令牌；运行包还要与虚构样例逐字节比对。
# 这是发布前检查，不宣称能自动识别任意个人信息；最终仍应检查公开文件清单。
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [ValidateSet('source','runtime')][string]$Kind = 'source',
    [string]$SampleRoot = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw '待检查目录不存在。' }
$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)
$problems = [Collections.Generic.List[string]]::new()
$manifest = @()
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
    if ($relative -eq 'public-audit.json') { continue }
    if ($relative -match '(^|/)(\.git|\.vs|obj|TestResults|local-data|data|logs|__pycache__)(/|$)' -or
        ($Kind -eq 'source' -and $relative -match '(^|/)(bin|SampleKnowledge|publish|release)(/|$)') -or
        $relative -match '(?i)\.(db(?:-wal|-shm)?|log|user|suo|zip)$' -or
        $file.Name -in @('appsettings.Workspace.json','appsettings.Local.json','settings.json')) {
        $problems.Add('不应公开的路径：' + $relative)
    }
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $problems.Add('不支持链接：' + $relative) }
    if ($file.Extension -in @('.cs','.csproj','.props','.json','.md','.txt','.ps1','.yml','.yaml','.sln','.slnLaunch') -or $file.Name -eq 'LICENSE') {
        $content = [IO.File]::ReadAllText($file.FullName)
        # 报告只记录路径与规则，不再次输出可能含密钥的匹配原文。
        if ($content -match '(?i)[A-Z]:[\\/]+Users[\\/]+[^%{\s]' -or $content -match '(?i)D:[\\/]+visual project[\\/]') { $problems.Add('开发者绝对路径：' + $relative) }
        # licenses 只由依赖许可导出器写入，须原样保留上游作者署名和联系邮箱；不能把这些声明当成用户资料删掉。
        if ($relative -notmatch '^licenses/' -and $content -match '(?i)\b[A-Z0-9._%+-]+@(?:qq\.com|163\.com|126\.com|gmail\.com|outlook\.com)\b') { $problems.Add('疑似个人邮箱：' + $relative) }
        if ($content -match '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,})') { $problems.Add('疑似访问令牌：' + $relative) }
    }
    $manifest += [ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
if ($Kind -eq 'runtime') {
    if ([string]::IsNullOrWhiteSpace($SampleRoot)) { throw '运行包检查必须提供虚构 SampleRoot。' }
    $approved = [IO.Path]::GetFullPath($SampleRoot)
    $actual = Join-Path $root 'service/SampleKnowledge'
    if (!(Test-Path -LiteralPath $actual -PathType Container)) { $problems.Add('缺少虚构示例目录。') }
    else {
        $actualFiles = @(Get-ChildItem -LiteralPath $actual -Recurse -File)
        $approvedFiles = @(Get-ChildItem -LiteralPath $approved -Recurse -File)
        if ($actualFiles.Count -ne $approvedFiles.Count) { $problems.Add('样例文件数量与批准目录不符。') }
        foreach ($file in $actualFiles) {
            $relative = [IO.Path]::GetRelativePath($actual, $file.FullName)
            $reference = [IO.Path]::GetFullPath((Join-Path $approved $relative))
            if (!$reference.StartsWith($approved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                !(Test-Path -LiteralPath $reference -PathType Leaf) -or
                (Get-FileHash -LiteralPath $reference).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                $problems.Add('不是批准的虚构样例：' + $relative)
            }
        }
    }
    # 两个入口都必须带本地运行时，不能只给客户端带运行时而让 API 依赖开发电脑。
    foreach ($entry in @($root, (Join-Path $root 'service'))) {
        foreach ($name in @('coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $entry $name))) { $problems.Add('缺少自带运行时：' + [IO.Path]::GetRelativePath($root, (Join-Path $entry $name))) }
        }
    }
}
if ($problems.Count) { throw ('公开副本检查失败：' + [Environment]::NewLine + ($problems -join [Environment]::NewLine)) }
[ordered]@{result='PASS';kind=$Kind;fileCount=$manifest.Count;files=$manifest} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $root 'public-audit.json') -Encoding UTF8
Write-Host "PUBLIC_AUDIT=PASS; KIND=$Kind; FILES=$($manifest.Count)"
