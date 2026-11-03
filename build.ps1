# Builds the DominoEX / THOR module and packages it for Kainos (Setup > Modules > Install from file, or a GitHub release
# listed in Kainos's modules.json).
#
#   .\build.ps1 [-KainosBin "<Kainos>\Project Files\bin\x64\Release\"] [-SkipTests]
#
# Writes dist\kainos-domino-<version>.zip and dist\registry-entry.json (the entry for modules.json, with the zip's
# SHA-256).

param(
    [string]$KainosBin = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found (install Visual Studio with .NET desktop development)" }
$props = @("/p:Configuration=Release", "/v:minimal", "/nologo")
if ($KainosBin) { $props += "/p:KainosBin=$KainosBin" }

$meta = Get-Content "$root\module.json" -Raw | ConvertFrom-Json
$ver = $meta.version
Write-Host "Building $($meta.name) $ver"
& $msbuild "$root\tests\Kainos.Domino.SelfTest\Kainos.Domino.SelfTest.csproj" @props
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipTests) {
    $testArgs = @()
    & "$root\tests\Kainos.Domino.SelfTest\bin\Release\Kainos.Domino.SelfTest.exe" @testArgs
    if ($LASTEXITCODE -ne 0) { throw "self-test failed" }
}

# stage: the module and its documents
$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$root\module.json", "$root\LICENSE", "$root\README.md" $stage
Copy-Item "$root\src\Kainos.Domino\bin\Release\Kainos.Domino.dll" $stage

# zip it (entries with forward slashes, module.json at the top)
$zip = Join-Path $root "dist\kainos-domino-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $rel, "Optimal") | Out-Null
    }
} finally { $z.Dispose() }

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
$entry = [ordered]@{
    id = $meta.id; name = $meta.name; description = $meta.description; experimental = [bool]$meta.experimental; version = $ver; api = $meta.api
    minKainos = $meta.minKainos; entry = $meta.entry; author = $meta.author; license = $meta.license; homepage = $meta.homepage
    url = "https://github.com/PNWHam-K7JUS/kainos-module-domino/releases/download/v$ver/kainos-domino-$ver.zip"
    sha256 = $sha; size = $size
}
$entry | ConvertTo-Json | Set-Content (Join-Path $root "dist\registry-entry.json") -Encoding utf8
Write-Host ""
Write-Host "Packaged $zip ($([math]::Round($size / 1KB)) KB)"
Write-Host "SHA-256 $sha"
