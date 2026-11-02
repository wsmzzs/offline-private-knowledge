# 用途：从实际 NuGet 还原结果导出依赖清单、原始包声明和随包许可文件，不包含 NuGet 缓存绝对路径。
param([Parameter(Mandatory=$true)][string]$BuildRoot, [Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
# NuGet 只声明许可表达式、没有附正文的库，使用仓库内已按上游提交固定的原文；构建时不额外联网获取许可。
Copy-Item -LiteralPath (Join-Path $BuildRoot 'licenses/upstream') -Destination (Join-Path $Destination 'upstream') -Recurse
$seen = @{}
$rows = @()
foreach ($project in @('PrivateKnowledge.Desktop','PrivateKnowledge.Api')) {
    $assets = Get-Content -LiteralPath (Join-Path $BuildRoot ('src/' + $project + '/obj/project.assets.json')) -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $seen.ContainsKey($library.Name)) { continue }
        $seen[$library.Name] = $true
        $parts = $library.Name.Split('/'); $id = $parts[0]; $version = $parts[1]
        $package = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = [IO.Path]::GetFullPath($candidate); break }
        }
        if (!$package) { throw ('找不到已还原依赖：' + $library.Name) }
        $target = Join-Path $Destination ($id + '.' + $version)
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $nuspec = Get-ChildItem -LiteralPath $package -File -Filter '*.nuspec' | Select-Object -First 1
        if (!$nuspec) { throw ('依赖缺少声明：' + $library.Name) }
        Copy-Item -LiteralPath $nuspec.FullName -Destination $target
        [xml]$spec = Get-Content -LiteralPath $nuspec.FullName -Raw
        $license = $spec.package.metadata.license
        if ($license -and $license.type -eq 'file') {
            $licensePath = [IO.Path]::GetFullPath((Join-Path $package $license.InnerText))
            if (!$licensePath.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $licensePath)) { throw ('依赖许可文件无效：' + $library.Name) }
            Copy-Item -LiteralPath $licensePath -Destination (Join-Path $target 'package-license.txt')
        }
        foreach ($notice in Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice|copying' }) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $target $notice.Name) -Force
        }
        $rows += [ordered]@{package=$id;version=$version;license=if($license){$license.InnerText}else{'see package declaration'};url=('https://www.nuget.org/packages/' + $id + '/' + $version)}
    }
    # 自带运行时是 SDK 下载的运行包，不一定列在普通 libraries；它的许可和第三方声明也必须保留。
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($dependency in $framework.Value.downloadDependencies) {
            if ($dependency.name -notmatch '^Microsoft\.(NETCore|AspNetCore)\.App\.Runtime\.win-x64$') { continue }
            # assets 中固定范围形如 [9.0.22, 9.0.22]；目录只使用其中的单个版本号。
            $bounds = $dependency.version.Trim('[',']').Split(',')
            $version = $bounds[0].Trim()
            if ($bounds.Count -ne 2 -or $version -ne $bounds[1].Trim()) { throw '运行时依赖没有固定为一个补丁版本。' }
            $id = $dependency.name
            if ($seen.ContainsKey($id + '/' + $version)) { continue }
            $seen[$id + '/' + $version] = $true
            $package = $null
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $candidate = Join-Path $folder ($id.ToLowerInvariant() + '/' + $version)
                if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
            }
            if (!$package) { throw ('找不到运行时许可目录：' + $id) }
            $target = Join-Path $Destination ($id + '.' + $version)
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            foreach ($notice in Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice|\.nuspec$' }) {
                Copy-Item -LiteralPath $notice.FullName -Destination $target
            }
            $rows += [ordered]@{package=$id;version=$version;license='see runtime notices';url=('https://www.nuget.org/packages/' + $id + '/' + $version)}
        }
    }
}
$rows | Sort-Object package | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'dependencies.json') -Encoding UTF8
'本项目采用 MIT；各依赖、原生组件、Ollama 和模型遵循各自许可证。此目录保留实际 NuGet 包声明和随包许可文件，元数据表达式请结合对应上游许可证阅读。' |
    Set-Content -LiteralPath (Join-Path $Destination 'README.txt') -Encoding UTF8
