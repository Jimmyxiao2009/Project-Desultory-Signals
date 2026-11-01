# build-app.ps1 -- build the ARM32 UWP debug shell into an appx.
#
# Uses VS2017's MSBuild (15.0) rather than `dotnet build`: the UWP 6.2.x toolchain
# of this vintage and the C# XAML markup compiler both live in the VS2017
# installation, and the modern `dotnet` CLI does not drive them.
#
# Why VS2017 specifically: the C++/CX XAML compiler is broken environment-wide on
# this machine (every installed SDK plus both VS2017 and VS18 MSBuild crash while
# generating XamlTypeInfo, even for an empty page). The C# markup compiler is a
# managed tool and is unaffected, which is why this shell is C#.
#
# Usage:
#   pwsh -NoProfile -File scripts\build-app.ps1
#   pwsh -NoProfile -File scripts\build-app.ps1 -Configuration Release

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Platform = 'ARM',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$NoRestore,
    [switch]$SkipSign,
    [string]$Password = 'desultory'
)

$ErrorActionPreference = 'Continue'

$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
$project = Join-Path $RepoRoot 'app\DesultorySignals\DesultorySignals.csproj'
$log = Join-Path $RepoRoot 'app\build-app.log'

if (-not (Test-Path $msbuild)) {
    Write-Host "[build-app] FATAL: VS2017 MSBuild not found at $msbuild" -ForegroundColor Red
    exit 2
}
if (-not (Test-Path $project)) {
    Write-Host "[build-app] FATAL: $project not found" -ForegroundColor Red
    exit 2
}

# The native runtime must be in place before the appx is assembled; the project
# declares libs\ARM\*.dll as Content, so a missing file produces an appx that
# installs and then fails to start.
$out = Join-Path $RepoRoot 'native\out'
foreach ($file in 'python-uwp.dll', 'z.dll') {
    $target = Join-Path $RepoRoot "app\DesultorySignals\libs\ARM\$file"
    $source = Join-Path $out $file
    if (-not (Test-Path $target)) {
        if (-not (Test-Path $source)) {
            Write-Host "[build-app] FATAL: $file is missing; run scripts\build-pythonhost.ps1 first" -ForegroundColor Red
            exit 2
        }
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item $source $target -Force
        Write-Host "[build-app] staged $file from native\out"
    }
}

$arguments = @(
    $project,
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    '/v:m',
    '/nologo',
    '/m'
)
if (-not $NoRestore) { $arguments += '/restore' }

Write-Host "[build-app] $Configuration|$Platform"
& $msbuild @arguments *> $log
$code = $LASTEXITCODE

Write-Host "[build-app] EXIT=$code  log=$log"

if ($code -ne 0) {
    $errors = @(Select-String -Path $log -Pattern ': error ' -ErrorAction SilentlyContinue)
    Write-Host "[build-app] $($errors.Count) errors:"
    $errors | Select-Object -First 30 | ForEach-Object { Write-Host "  $($_.Line)" }
    exit $code
}

$packages = Join-Path $RepoRoot 'app\DesultorySignals\AppPackages'
$appx = Get-ChildItem $packages -Recurse -Filter '*.appx' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'DesultorySignals_*.appx' -and $_.FullName -notmatch '[\\/]Dependencies[\\/]' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $appx) {
    Write-Host '[build-app] the build reported success but produced no appx' -ForegroundColor Yellow
    exit 1
}

Write-Host "[build-app] built $($appx.FullName)"
Write-Host "[build-app]   $($appx.Length) bytes, $($appx.LastWriteTime)"

# ARM32 was removed from the machine's current VCLibs ExtensionSDK. Carry the
# original Microsoft-signed retail framework alongside the application instead.
# Archived artifact: Microsoft.VCLibs.140.00 14.0.27323.0, ARM, MinOS 10042.
if ($Platform -eq 'ARM') {
    $vcPackage = Join-Path $RepoRoot 'app\DesultorySignals\libs\ARM\Microsoft.VCLibs.ARM.14.00.appx'
    $vcHash = 'B0F372ECEF8A8962867232ACBDEFF81C05A620675ED3817B758CFF1607B6DE41'
    if (-not (Test-Path -LiteralPath $vcPackage)) {
        $vcUrl = 'https://raw.githubusercontent.com/WOA-Project/Support-Desktop-Drivers/master/Applications/arm/Microsoft.VCLibs.ARM.14.00.appx'
        Invoke-WebRequest -Uri $vcUrl -OutFile $vcPackage -ErrorAction Stop
    }
    if ((Get-FileHash -LiteralPath $vcPackage -Algorithm SHA256).Hash -ne $vcHash) {
        throw 'The ARM32 VCLibs package does not match the verified Microsoft artifact.'
    }
    $vcSignTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $vcSignTool) { throw 'signtool is required to verify the VCLibs framework.' }
    & $vcSignTool.FullName verify /pa $vcPackage *>> $log
    if ($LASTEXITCODE -ne 0) { throw 'The ARM32 VCLibs Microsoft signature verification failed.' }
    $dependencyDir = Join-Path $appx.DirectoryName 'Dependencies\ARM'
    New-Item -ItemType Directory -Path $dependencyDir -Force | Out-Null
    Copy-Item -LiteralPath $vcPackage -Destination $dependencyDir -Force
    Write-Host '[build-app] staged verified ARM32 retail VCLibs dependency'
}

# --- sign ---------------------------------------------------------------------
# Done here rather than in the csproj because MSBuild 15's AppxPackaging cannot
# import a modern PFX (APPX0105) while signtool handles it without complaint. An
# unsigned appx cannot be sideloaded, so this step is what makes the output usable.
if (-not $SkipSign) {
    $pfx = Join-Path $RepoRoot 'app\DesultorySignals\DesultorySignals_TemporaryKey.pfx'
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1

    if (-not (Test-Path $pfx)) {
        Write-Host "[build-app] WARNING: no signing certificate at $pfx; the appx is unsigned and cannot be sideloaded" -ForegroundColor Yellow
    } elseif (-not $signtool) {
        Write-Host '[build-app] WARNING: signtool.exe not found; the appx is unsigned' -ForegroundColor Yellow
    } else {
        & $signtool.FullName sign /fd SHA256 /f $pfx /p $Password $appx.FullName *>> $log
        if ($LASTEXITCODE -eq 0) {
            Write-Host '[build-app] signed'
            & $signtool.FullName verify /pa $appx.FullName *>> $log
            Write-Host "[build-app] signature verify EXIT=$LASTEXITCODE"
        } else {
            Write-Host "[build-app] signing FAILED EXIT=$LASTEXITCODE (see the log)" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
}

$warnings = @(Select-String -Path $log -Pattern ': warning ' -ErrorAction SilentlyContinue)
if ($warnings.Count -gt 0) {
    Write-Host "[build-app] $($warnings.Count) warnings (see the log)"
}

exit 0
