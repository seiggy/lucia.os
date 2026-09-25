param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = [System.IO.Path]::Combine($repo, 'src', 'Lucia.Desktop', 'Lucia.Desktop.csproj')
$destination = if ($OutputDirectory) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::Combine($repo, 'artifacts', 'desktop', $Runtime)
}
$publishDirectory = if ($Runtime.StartsWith('osx-')) {
    [System.IO.Path]::Combine($destination, 'Lucia.app', 'Contents', 'MacOS')
} else {
    $destination
}

$frontend = Join-Path $repo 'src\frontend'
Push-Location $frontend
try {
    npm run build
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath (Join-Path $frontend 'node_modules')) {
            throw 'Frontend build failed. Resolve its diagnostics before packaging the desktop.'
        }
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed after restoring missing dependencies.' }
    }
} finally {
    Pop-Location
}

# A new staging path prevents stale publish files from entering the immutable host artifact.
$staging = Join-Path $repo ('artifacts\desktop-host\' + [Guid]::NewGuid().ToString('N'))
$serverOutput = Join-Path $staging 'publish'
$hostOutput = Join-Path $staging 'HostPayload'
$server = Join-Path $repo 'src\Lucia.Homelab.Server\Lucia.Homelab.Server.csproj'
dotnet publish $server --configuration Release --runtime linux-arm64 --self-contained false `
    --output $serverOutput --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    throw 'ARM64 host publish failed. The maintainer build requires the configured TensorSharp package feed.'
}
$nodeAgent = Join-Path $repo 'src\Lucia.NodeAgent\Lucia.NodeAgent.csproj'
dotnet publish $nodeAgent --configuration Release --runtime linux-x64 --self-contained true `
    --output (Join-Path $serverOutput 'boot\node-agent-linux-x64') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'The x64 read-only discovery agent could not be packaged.' }
$webroot = Join-Path $serverOutput 'wwwroot'
New-Item -ItemType Directory -Force -Path $webroot | Out-Null
Get-ChildItem -LiteralPath (Join-Path $frontend 'dist') | Copy-Item -Destination $webroot -Recurse -Force
python (Join-Path $repo 'tools\host\package.py') $serverOutput `
    (Join-Path $hostOutput 'host-linux-arm64.tar.gz') --manifest (Join-Path $hostOutput 'manifest.json')
if ($LASTEXITCODE -ne 0) { throw 'Managed host packaging failed; no desktop package is ready.' }

dotnet publish $project --configuration Release --runtime $Runtime --self-contained true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false --output $publishDirectory --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    throw "Desktop publish failed for $Runtime."
}
$bundledHost = Join-Path $publishDirectory 'HostPayload'
New-Item -ItemType Directory -Force -Path $bundledHost | Out-Null
Copy-Item -LiteralPath (Join-Path $hostOutput 'host-linux-arm64.tar.gz'), (Join-Path $hostOutput 'manifest.json') -Destination $bundledHost -Force
if ($Runtime.StartsWith('osx-')) {
    $plist = [System.IO.Path]::Combine($repo, 'deployment', 'desktop', 'Info.plist')
    Copy-Item -LiteralPath $plist -Destination (Split-Path $publishDirectory -Parent)
}

Write-Output "Unsigned development build: $destination"
Write-Output 'Includes the verified Linux ARM64 host + dashboard. No model weights or Hugging Face tokens are packaged.'
Write-Output 'Includes the x64 read-only discovery runtime; this does not enable PXE or authorize disk installation.'
Write-Output "Maintainer staging outputs retained at $staging"
Write-Output 'Build final distributables on their target OS, then sign/notarize them before distribution.'
