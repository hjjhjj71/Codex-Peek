param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 自带的 .NET Framework 4.8 C# 编译器。' }
$buildRoot = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll')
$common = @('/nologo', '/langversion:5', '/optimize+', '/warn:4', '/warnaserror+', '/platform:anycpu') + $references
& $compiler @common /target:winexe ("/win32manifest:" + (Join-Path $projectRoot 'app.manifest')) ("/out:" + (Join-Path $buildRoot 'CodexPeek.exe')) @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '应用编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'CodexPeek.exe.config') -Destination $buildRoot -Force
Write-Output 'CodexPeek.exe 构建成功'
if ($Test) {
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @common /target:exe /main:CodexPeek.Tests.TestProgram ("/out:" + (Join-Path $buildRoot 'CodexPeek.Tests.exe')) @sourceFiles @testFiles
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CodexPeek.exe.config') -Destination (Join-Path $buildRoot 'CodexPeek.Tests.exe.config') -Force
    & (Join-Path $buildRoot 'CodexPeek.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
}
