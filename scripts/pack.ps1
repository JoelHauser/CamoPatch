<#
.SYNOPSIS
    Builds Release and packs dist\NoMagazineCamo_V<ver>.zip.

.DESCRIPTION
    The zip is laid out to extract straight into an SPT folder:

        BepInEx\plugins\NoMagazineCamo\NoMagazineCamo.Client.dll

    Nothing else. No README at the top of the zip: a loose file in an archive meant
    to be extracted over an SPT folder lands in the install root, where it is litter.

    It checks that both version strings agree before building anything -- the
    csproj <Version> and NoMagazineCamoPlugin.PluginVersion.

    It writes the zip through System.IO.Compression with forward-slash entry names.
    Compress-Archive writes backslashes, which extract on Linux as one file with
    slashes in its name rather than as a tree.

.PARAMETER SPTPath
    The SPT install to build against. The plugin is compiled against the game's own
    assemblies, so this decides which EFT build it will load on.

.PARAMETER CamoModDir
    The folder holding 7Bpencil.WeaponCamoAndStickers.dll, if it is not installed
    into SPTPath. The plugin compiles against it.

.PARAMETER GameAssembly
    A patched Assembly-CSharp.dll to compile against instead of the one in SPTPath's
    Managed folder. Needed only while that install has never been started through the
    SPT Launcher, which is what applies SPT's patch to it. See CLAUDE.md.

.PARAMETER Install
    Also copy the DLL into BepInEx\plugins\NoMagazineCamo under SPTPath.

.EXAMPLE
    scripts\pack.ps1 -SPTPath D:\SPT
    scripts\pack.ps1 -SPTPath D:\SPT -Install
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SPTPath,
    [string]$CamoModDir,
    [string]$GameAssembly,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'src\NoMagazineCamo.Client'

function Get-Match {
    param([string]$Path, [string]$Pattern)

    $text = Get-Content -Raw -Path (Join-Path $root $Path)
    $found = [regex]::Match($text, $Pattern)
    if (-not $found.Success) { throw "no version found in $Path" }
    return $found.Groups[1].Value
}

# ---------------------------------------------------------------- the version

$versions = [ordered]@{
    'NoMagazineCamo.Client.csproj' = Get-Match 'src\NoMagazineCamo.Client\NoMagazineCamo.Client.csproj' '<Version>([^<]+)</Version>'
    'NoMagazineCamoPlugin.cs'      = Get-Match 'src\NoMagazineCamo.Client\NoMagazineCamoPlugin.cs' 'PluginVersion\s*=\s*"([^"]+)"'
}

# Forced to an array: a single string indexes as characters.
$distinct = @($versions.Values | Select-Object -Unique)
if ($distinct.Count -ne 1) {
    $versions.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-30} {1}" -f $_.Key, $_.Value) }
    throw "the version strings disagree"
}

$version = $distinct[0]
Write-Host "No Magazine Camo $version" -ForegroundColor Cyan

# ------------------------------------------------------------------ the build

$buildArgs = @("-p:SPTPath=$SPTPath")
if ($CamoModDir) { $buildArgs += "-p:CamoModDir=$CamoModDir" }
if ($GameAssembly) { $buildArgs += "-p:GameAssembly=$GameAssembly" }

dotnet build (Join-Path $projectDir 'NoMagazineCamo.Client.csproj') -c Release --nologo -v q @buildArgs
if ($LASTEXITCODE -ne 0) { throw "the build failed" }

$dll = Join-Path $projectDir 'bin\Release\NoMagazineCamo.Client.dll'
if (-not (Test-Path $dll)) { throw "built, but no DLL at $dll" }

# ------------------------------------------------------------------ the stage

$stage = Join-Path $root 'dist\stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

$pluginDir = Join-Path $stage 'BepInEx\plugins\NoMagazineCamo'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item $dll $pluginDir

# -------------------------------------------------------------------- the zip

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# dist\ is gitignored: releases are published through GitHub Releases, not committed.
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$zipPath = Join-Path $dist ("NoMagazineCamo_V{0}.zip" -f $version)
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($file in Get-ChildItem -Recurse -File -Path $stage) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal') | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Remove-Item -Recurse -Force $stage
Write-Host "packed $zipPath" -ForegroundColor Green

# -------------------------------------------------------------- the install

if ($Install) {
    $destination = Join-Path $SPTPath 'BepInEx\plugins\NoMagazineCamo'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item $dll $destination -Force
    Write-Host "installed to $destination" -ForegroundColor Green
}
