$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet run --project tests/Moyu.Tests/Moyu.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '集成检查失败。' }

    $releaseDirectory = Join-Path $projectRoot 'dist/MoyuReader-win-x64'
    dotnet publish src/Moyu.Desktop/Moyu.Desktop.csproj -c Release -r win-x64 --self-contained true -o $releaseDirectory -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/使用说明.txt') -Destination $releaseDirectory -Force
    foreach ($subdirectory in @('books', 'config', 'progress')) {
        New-Item -ItemType Directory -Path (Join-Path $releaseDirectory "resources/$subdirectory") -Force | Out-Null
    }

    # Stage a clean package without copying any personal resource data from a used executable folder.
    $packageStage = Join-Path $projectRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
    $portableStage = Join-Path $packageStage 'MoyuReader-win-x64'
    New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
    Get-ChildItem -LiteralPath $releaseDirectory | Where-Object { $_.Name -ne 'resources' -and $_.Name -ne 'validation' -and $_.Name -notlike '*fixture*' -and $_.Name -notlike 'smoke-*' } | Copy-Item -Destination $portableStage -Recurse
    foreach ($subdirectory in @('books', 'config', 'progress')) { New-Item -ItemType Directory -Path (Join-Path $portableStage "resources/$subdirectory") -Force | Out-Null }
    Compress-Archive -Path $portableStage -DestinationPath 'dist/MoyuReader-v0.1.0-win-x64.zip' -Force

    $sourceStage = Join-Path $packageStage 'MoyuReader-source'
    New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    foreach ($folder in @('src', 'tests', 'scripts', 'docs')) {
        $baseDirectory = Join-Path $projectRoot $folder
        Get-ChildItem -LiteralPath $baseDirectory -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
            $relative = $_.FullName.Substring($projectRoot.Length + 1)
            $destination = Join-Path $sourceStage $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
    foreach ($file in @('README.md', 'GLOSSARY.md', '.gitignore', '.gitattributes')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $sourceStage }
    Compress-Archive -Path $sourceStage -DestinationPath 'dist/MoyuReader-v0.1.0-source.zip' -Force
    Get-FileHash 'dist/MoyuReader-v0.1.0-win-x64.zip', 'dist/MoyuReader-v0.1.0-source.zip' -Algorithm SHA256 | Format-Table -AutoSize
}
finally { Pop-Location }
