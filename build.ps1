$ErrorActionPreference = 'Stop'
# The source tree may sit directly in the game folder or in a subfolder of it,
# so walk up until DerailValley_Data is found instead of assuming the parent.
$game = Split-Path -Parent $PSScriptRoot
while ($game -and -not (Test-Path -LiteralPath (Join-Path $game 'DerailValley_Data\Managed'))) {
    $game = Split-Path -Parent $game
}
if (-not $game) { throw 'Could not locate the Derail Valley folder above this script' }
$managed = Join-Path $game 'DerailValley_Data\Managed'
$output = Join-Path $PSScriptRoot 'bin\RailwayBrakeShoe.dll'
New-Item -ItemType Directory -Force (Split-Path -Parent $output) | Out-Null

$references = @(
    "$managed\UnityModManager\0Harmony.dll",
    "$managed\UnityModManager\UnityModManager.dll",
    "$managed\DV.UserManagement.dll",
    "$game\Mods\custom_item_mod\custom_item_mod.dll",
    "$managed\Assembly-CSharp.dll",
    "$managed\DV.CabControls.Spec.dll",
    "$managed\DV.Common.dll",
    "$managed\DV.Interaction.dll",
    "$managed\DV.Telemetry.dll",
    "$managed\DV.RailTrack.dll",
    "$managed\BezierCurves.dll",
    "$managed\DV.MeshX.dll",
    "$managed\DV.PointSet.dll",
    "$managed\net.smkd.vector3d.dll",
    "$managed\DV.ThingTypes.dll",
    "$managed\DV.Inventory.dll",
    "$managed\DV.Utils.dll",
    "$managed\DV.BrakeSystem.dll",
    "$managed\DV.Simulation.dll",
    "$managed\WorldStreamer.dll",
    "$managed\DV.OriginShiftInfo.dll",
    "$managed\LINQtoGameObject.dll",
    "$managed\Newtonsoft.Json.dll",
    "$managed\netstandard.dll",
    "$managed\UnityEngine.dll",
    "$managed\UnityEngine.CoreModule.dll",
    "$managed\UnityEngine.IMGUIModule.dll",
    "$managed\UnityEngine.PhysicsModule.dll",
    "$managed\UnityEngine.ParticleSystemModule.dll",
    "$managed\UnityEngine.ImageConversionModule.dll",
    "$managed\UnityEngine.InputLegacyModule.dll",
    "$managed\UnityEngine.AudioModule.dll",
    "$managed\DV.NAudio.dll"
)
$referenceArgs = $references | ForEach-Object { "/reference:$_" }
$sources = @('Main.cs', 'ShoePhysics.cs', 'Integrations.cs', 'ShoeRules.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /langversion:5 /target:library /optimize+ "/out:$output" $referenceArgs $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$install = Join-Path $game 'Mods\RailwayBrakeShoe'
New-Item -ItemType Directory -Force (Join-Path $install 'Assets') | Out-Null
Copy-Item -LiteralPath $output -Destination (Join-Path $install 'RailwayBrakeShoe.dll') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'info.json') -Destination (Join-Path $install 'info.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $install 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RESEARCH.md') -Destination (Join-Path $install 'RESEARCH.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST_REPORT.md') -Destination (Join-Path $install 'TEST_REPORT.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination (Join-Path $install 'CHANGELOG.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\railway_brake_shoe_final.glb') -Destination (Join-Path $install 'Assets\railway_brake_shoe_final.glb') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\railway_brake_shoe_icon.png') -Destination (Join-Path $install 'Assets\railway_brake_shoe_icon.png') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\MetalSolid.wav') -Destination (Join-Path $install 'Assets\MetalSolid.wav') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\BrakeShoes.wav') -Destination (Join-Path $install 'Assets\BrakeShoes.wav') -Force
# Harmony/UMM can leave a patched assembly cache beside the mod DLL. Remove
# caches tied to older builds so the next launch must patch the current binary.
Get-ChildItem -LiteralPath $install -Filter 'RailwayBrakeShoe.dll.*.cache' -File -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Force
}
Remove-Item -LiteralPath (Join-Path $install 'Assets\railway_brake_shoe_game.glb') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $install 'Assets\railway_brake_shoe_baseColor.jpg') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $install 'Assets\railway_brake_shoe___scan.glb') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $install 'Assets\rail_skid_brake_shoe.glb') -Force -ErrorAction SilentlyContinue
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
# Version comes from info.json so the archive name cannot drift from the
# version the mod manager actually reports.
$modVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'info.json') -Raw | ConvertFrom-Json).Version
if (-not $modVersion) { throw 'Could not read Version from info.json' }
$archive = Join-Path $dist "RailwayBrakeShoe-$modVersion.zip"
$stageRoot = Join-Path $PSScriptRoot 'bin\package'
$stageInstall = Join-Path $stageRoot 'RailwayBrakeShoe'
if (Test-Path -LiteralPath $stageRoot) {
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    $resolvedBin = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($resolvedBin, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe package staging path: $resolvedStage"
    }
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $stageInstall 'Assets') | Out-Null
foreach ($file in @('RailwayBrakeShoe.dll', 'info.json', 'README.md', 'RESEARCH.md', 'TEST_REPORT.md', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $install $file) -Destination (Join-Path $stageInstall $file) -Force
}
foreach ($asset in @('railway_brake_shoe_final.glb', 'railway_brake_shoe_icon.png', 'MetalSolid.wav', 'BrakeShoes.wav')) {
    Copy-Item -LiteralPath (Join-Path $install "Assets\$asset") -Destination (Join-Path $stageInstall "Assets\$asset") -Force
}
Compress-Archive -LiteralPath $stageInstall -DestinationPath $archive -Force
Write-Host "Built and installed $output"
