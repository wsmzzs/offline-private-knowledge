param(
    # 原始资料根目录；不填写时使用项目内的虚构样例，每个子文件夹是一套知识库。
    [string]$KnowledgeRoot = '',
    # 索引目录；真实部署时建议放在代码仓库之外。
    [string]$DataRoot = (Join-Path $PSScriptRoot 'data')
)
# 用途：用绝对路径启动服务，避免从不同工作目录运行时找错资料。
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($KnowledgeRoot)) {
    # 已有本机私库继续沿用；公开源码不含私库时使用固定的虚构样例。
    $privateRoot = Join-Path $PSScriptRoot 'SampleKnowledge'
    $KnowledgeRoot = if (Test-Path -LiteralPath $privateRoot -PathType Container) { $privateRoot } else { Join-Path $PSScriptRoot 'PublicSamples' }
}
$sourceRoot = [IO.Path]::GetFullPath($KnowledgeRoot)
$indexRoot = [IO.Path]::GetFullPath($DataRoot)
$projectFile = Join-Path $PSScriptRoot 'src\PrivateKnowledge.Api\PrivateKnowledge.Api.csproj'
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "知识资料目录不存在：$sourceRoot。请先建立一级项目子文件夹并放入 UTF-8 TXT/MD、DOCX、文字型 PDF 或 XLSX。"
}
# -- 后的参数交给服务；关闭自动启动配置，确保以这里明确指定的路径为准。
# 依赖按 README 首次还原后，日常启动不再检查网络，便于断网使用。
Write-Host "资料目录：$sourceRoot"
Write-Host "索引目录：$indexRoot"
Write-Host '接口页面：http://127.0.0.1:5088/swagger ；按 Ctrl+C 停止服务。'
& dotnet run --project $projectFile --no-restore --no-launch-profile -- --Knowledge:RootPath $sourceRoot --Knowledge:DataPath $indexRoot
exit $LASTEXITCODE
