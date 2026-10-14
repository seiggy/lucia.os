# Builds the node agent, the host artifact and the qualification controller from the repo.
# Usage: .\build.ps1 -Controller managed-controller7
param([Parameter(Mandatory)][string]$Controller)
$ErrorActionPreference = 'Stop'
$repo = 'E:\github\lucia-os'
$files = $PSScriptRoot
$agent = "$files\managed-node-agent"
$sandbox = "$files\skill-sandbox"
$publish = "$files\spark-updates\publish"

Push-Location "$repo\src\frontend"; npm run build --silent; if ($LASTEXITCODE) { throw 'frontend build failed' }; Pop-Location

Remove-Item $agent -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$repo\src\Lucia.NodeAgent" -c Release -r linux-x64 --self-contained -o $agent -v q -nologo
if ($LASTEXITCODE) { throw 'agent publish failed' }
Remove-Item "$files\managed-node-agent.tar.gz" -ErrorAction SilentlyContinue
tar -czf "$files\managed-node-agent.tar.gz" -C $agent .

Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$repo\src\Lucia.SkillSandbox" -c Release -r linux-x64 --self-contained -o $sandbox -v q -nologo
if ($LASTEXITCODE) { throw 'skill sandbox publish failed' }

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$repo\src\Lucia.Homelab.Server" -c Release -r linux-arm64 --self-contained false -o $publish -v q -nologo
if ($LASTEXITCODE) { throw 'server publish failed' }
Remove-Item "$publish\wwwroot" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$repo\src\frontend\dist" "$publish\wwwroot" -Recurse
New-Item "$publish\boot" -ItemType Directory -Force | Out-Null
Copy-Item $agent "$publish\boot\node-agent-linux-x64" -Recurse
Copy-Item $sandbox "$publish\boot\skill-sandbox-linux-x64" -Recurse
Remove-Item "$files\spark-updates\host-linux-arm64.tar.gz", "$files\spark-updates\manifest.json" -ErrorAction SilentlyContinue
$env:PYTHONIOENCODING = 'utf-8'
python "$repo\tools\host\package.py" $publish "$files\spark-updates\host-linux-arm64.tar.gz" --manifest "$files\spark-updates\manifest.json"
if ($LASTEXITCODE) { throw 'package failed' }

$controllerDir = "$files\$Controller"
Remove-Item $controllerDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$repo\tests\Lucia.ManagedNode.Qualification" -c Release -r linux-arm64 --self-contained -o $controllerDir -v q -nologo
if ($LASTEXITCODE) { throw 'controller publish failed' }
Remove-Item "$controllerDir.tar.gz" -ErrorAction SilentlyContinue
tar -czf "$controllerDir.tar.gz" -C $controllerDir .

$server = (Get-FileHash "$publish\Lucia.Homelab.Server.dll").Hash.ToLower()
$qual = (Get-FileHash "$controllerDir\Lucia.Homelab.Server.dll").Hash.ToLower()
"artifact $((Get-Content "$files\spark-updates\manifest.json" | ConvertFrom-Json).sha256)"
"server.dll $server"
"controller server.dll $qual $(if ($server -eq $qual) { 'MATCH' } else { 'DIFFERS' })"
