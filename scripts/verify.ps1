$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet run --project tests/Moyu.Tests/Moyu.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '核心集成检查失败。' }
    $releaseDirectory = Join-Path $projectRoot 'dist/MoyuReader-win-x64'
    if (-not (Test-Path (Join-Path $releaseDirectory 'MoyuReader.exe'))) { throw '请先运行 scripts/build.ps1。' }
    $testDirectory = Join-Path $projectRoot ('artifacts/validation-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $releaseDirectory | Where-Object { $_.Name -ne 'resources' -and $_.Name -ne 'validation' } | Copy-Item -Destination $testDirectory -Recurse
    $testProcess = Start-Process -FilePath (Join-Path $testDirectory 'MoyuReader.exe') -ArgumentList '--smoke-test' -PassThru -WindowStyle Hidden
    if (-not $testProcess.WaitForExit(60000)) { throw "窗口检查超时。诊断目录：$testDirectory" }
    $result = Get-Content -LiteralPath (Join-Path $testDirectory 'smoke-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $result.checks
    if (-not $result.success) { throw $result.error }
    Write-Output "窗口检查通过，渲染图和报告：$testDirectory"
}
finally { Pop-Location }
