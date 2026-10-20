param(
    [string]$DungeonHelperDir = "$env:APPDATA\Dungeon Helper"
)

$pluginDir = "$DungeonHelperDir\plugins\RotationHelper"
$buildOutput = "$PSScriptRoot\RotationHelper\bin\Release\net8.0-windows"

Write-Host "Deploying RotationHelper plugin..." -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $pluginDir)) {
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    Write-Host "  Created directory: $pluginDir" -ForegroundColor Green
}

Copy-Item -Path "$buildOutput\RotationHelper.dll" -Destination "$pluginDir\RotationHelper.dll" -Force
Copy-Item -Path "$buildOutput\RotationHelper.deps.json" -Destination "$pluginDir\RotationHelper.deps.json" -Force
if (-not (Test-Path -LiteralPath "$pluginDir\RotationHelper.json")) {
    Copy-Item -Path "$PSScriptRoot\RotationHelper.json" -Destination "$pluginDir\RotationHelper.json" -Force
} else {
    Write-Host "  Config already exists, skipping overwrite" -ForegroundColor Yellow
}
Copy-Item -Path "$PSScriptRoot\plugin.metadata" -Destination "$pluginDir\plugin.metadata" -Force

$vokSource = "$DungeonHelperDir\VoK.Sdk.dll"
if (Test-Path -LiteralPath $vokSource) {
    Copy-Item -Path $vokSource -Destination "$pluginDir\VoK.Sdk.dll" -Force
    Write-Host "  Copied VoK.Sdk.dll" -ForegroundColor Green
} else {
    Write-Host "  WARNING: VoK.Sdk.dll not found at $vokSource" -ForegroundColor Yellow
}

Write-Host "  Copied plugin files" -ForegroundColor Green

$pluginsConfigPath = "$DungeonHelperDir\DungeonHelper.Plugins.json"
if (Test-Path -LiteralPath $pluginsConfigPath) {
    $pluginsConfig = Get-Content -Path $pluginsConfigPath -Raw | ConvertFrom-Json

    $existing = $pluginsConfig.Ddo | Where-Object { $_.AssemblyFile -eq "$pluginDir\RotationHelper.dll" -and $_.TypeName -eq "RotationHelper.RotationHelperPlugin" }
    if ($existing) {
        foreach ($entry in $pluginsConfig.Ddo) {
            if ($entry.AssemblyFile -eq "$pluginDir\RotationHelper.dll" -and $entry.TypeName -eq "RotationHelper.RotationHelperPlugin") {
                $entry.Enabled = $true
                Write-Host "  Updated existing plugin entry" -ForegroundColor Green
                break
            }
        }
    } else {
        $entry = [PSCustomObject]@{
            Enabled = $true
            AssemblyFile = "$pluginDir\RotationHelper.dll"
            TypeName = "RotationHelper.RotationHelperPlugin"
            PluginId = "B1C2D3E4-F5A6-7890-1234-56789ABCDEF0"
            Name = "Rotation Helper"
            Version = "1.0.0.0"
            Description = "Setup skill rotations and cycle through them while holding a key"
            Author = "Rotation Helper"
        }
        $pluginsConfig.Ddo += $entry
        Write-Host "  Added plugin entry to DungeonHelper.Plugins.json" -ForegroundColor Green
    }
    $pluginsConfig | ConvertTo-Json -Depth 10 | Set-Content -Path $pluginsConfigPath
} else {
    Write-Host "  WARNING: DungeonHelper.Plugins.json not found" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Deployment complete!" -ForegroundColor Cyan
Write-Host "Plugin installed at: $pluginDir" -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Restart Dungeon Helper if it's running" -ForegroundColor Yellow
Write-Host "  2. Click the black 'R' icon to open settings" -ForegroundColor Yellow
Write-Host "  3. Click 'Setup Skills' to create your rotation" -ForegroundColor Yellow
Write-Host "  4. Hold the trigger key to cycle through abilities" -ForegroundColor Yellow
