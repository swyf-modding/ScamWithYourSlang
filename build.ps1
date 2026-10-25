<#
    Builds ScamWYF.Translate.dll and drops it in BepInEx\plugins.

    No .NET SDK is required. Point -CscDll at a Roslyn csc.dll and it is run with `dotnet`;
    otherwise the script looks for csc.exe from Visual Studio Build Tools.

      .\build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Scam With Your Friends"
      .\build.ps1 -CscDll C:\path\to\csc.dll -NoCopy
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$CscDll,
    [switch]$NoCopy,
    [switch]$NoBundle
)

$ErrorActionPreference = 'Stop'

if (-not $GameDir) {
    # The usual layout is <game>\_ModDev\Translate, so two levels up is the game.
    $GameDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
$core    = Join-Path $GameDir 'BepInEx\core'
$plugins = Join-Path $GameDir 'BepInEx\plugins'
$outDir  = Join-Path $PSScriptRoot 'bin'

if (-not (Test-Path $managed)) { throw "Game assemblies not found at: $managed`nPass -GameDir <path to the game folder>." }
if (-not (Test-Path $core))    { throw "BepInEx not found at: $core" }

# mscorlib comes from the GAME, together with -nostdlib. The compiler then sees exactly the API
# surface Unity shipped, so it is impossible to write code against a method that managed stripping
# removed - which is what lets this plugin run without an unstripped corlib of its own.
$refs = @(
    "$managed\mscorlib.dll"
    "$managed\System.dll"
    "$managed\System.Core.dll"
    "$managed\UnityEngine.dll"
    "$managed\UnityEngine.CoreModule.dll"
    "$managed\UnityEngine.TextRenderingModule.dll"
    "$managed\Unity.Localization.dll"
    "$managed\Unity.ResourceManager.dll"
    "$managed\Unity.InputSystem.dll"
    "$managed\Newtonsoft.Json.dll"
    "$managed\ScriptsAssDef.dll"
    "$managed\Kolkata.Speech.dll"
    "$core\BepInEx.dll"
    "$core\0Harmony.dll"
)

foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference assembly: $r" } }

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No sources in $PSScriptRoot\src" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir 'ScamWYF.Translate.dll'

# The packs travel inside the DLL, so that installing the mod is one file: the launcher can only
# put DLLs in plugins, and a download that is a DLL plus a folder tree is a download people get
# half-right. EmbeddedPacks writes them back out under BepInEx\config on first run, where they are
# ordinary editable files again. One gzipped JSON object: path -> file text.
$bundle = Join-Path $outDir 'packs.bundle'
$packFiles = [ordered]@{}
if (-not $NoBundle) {
    foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'packs') -Recurse -File) {
        $relative = $file.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/')
        $packFiles[$relative] = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    }
    if ($packFiles.Count -eq 0) { throw "No packs in $PSScriptRoot\packs" }

    $json   = $packFiles | ConvertTo-Json -Depth 2 -Compress
    $raw    = [Text.Encoding]::UTF8.GetBytes($json)
    $memory = New-Object IO.MemoryStream
    $gzip   = New-Object IO.Compression.GZipStream($memory, [IO.Compression.CompressionMode]::Compress)
    $gzip.Write($raw, 0, $raw.Length)
    $gzip.Dispose()
    [IO.File]::WriteAllBytes($bundle, $memory.ToArray())
    $memory.Dispose()
    Write-Host ("Packed {0} pack files into {1} KB" -f $packFiles.Count, [math]::Round((Get-Item $bundle).Length / 1KB))
}

$cscArgs = @(
    '-target:library'
    "-out:$outDll"
    '-nostdlib+'
    '-noconfig'
    '-optimize+'
    '-langversion:7.3'
    '-warn:4'
    '-nologo'
    '-debug:portable'
)
if (-not $NoBundle) { $cscArgs += "-resource:$bundle,ScamWYF.Translate.packs.json.gz" }
$cscArgs += ($refs | ForEach-Object { "-r:$_" })
$cscArgs += $sources

if ($CscDll) {
    Write-Host "Compiler: dotnet $CscDll"
    & dotnet $CscDll @cscArgs
} else {
    $exe = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio", "$env:ProgramFiles\Microsoft Visual Studio" `
        -Filter csc.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*Roslyn*' } | Select-Object -First 1
    if (-not $exe) { throw "No compiler found. Pass -CscDll <path to Roslyn csc.dll>, or install VS Build Tools / the .NET SDK." }
    Write-Host "Compiler: $($exe.FullName)"
    & $exe.FullName @cscArgs
}
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)" }
Write-Host "Built $outDll" -ForegroundColor Green

if (-not $NoCopy) {
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item $outDll $plugins -Force
    $pdb = [IO.Path]::ChangeExtension($outDll, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb $plugins -Force }
    Write-Host "Installed to $plugins" -ForegroundColor Green
}
