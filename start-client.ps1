# 用途：从任意工作目录启动 Avalonia 客户端。首次先按 README 还原依赖，日常启动不联网检查 NuGet。
$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'src\PrivateKnowledge.Desktop\PrivateKnowledge.Desktop.csproj'
Write-Host '正在启动离线私库；本机模式会自动准备配套问答环境。'
# 本机退出只停止自己创建的问答程序；企业服务和已有程序保持运行。
& dotnet run --project $projectFile --no-restore
exit $LASTEXITCODE
