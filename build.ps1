<#
.SYNOPSIS
    Builds Klippy for Windows in Release with NativeAOT.

.DESCRIPTION
    Publishes the desktop head as a self-contained NativeAOT binary: no JIT, fast cold
    start, and the smallest output the project can produce.

    NativeAOT needs a native linker from the Visual Studio "Desktop development with C++"
    workload. That is checked up front, because otherwise the failure arrives several
    minutes into the build as a bare "Platform linker not found".

    Pass -NoAot to fall back to trimmed + ReadyToRun, which needs no C++ toolchain and
    still starts quickly, at the cost of a much larger self-contained output.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Runtime win-arm64 -Clean -Test
    .\build.ps1 -NoAot          # no C++ toolchain available
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    # Where to place the published app. Defaults to the project's own publish folder.
    [string] $Output,

    # Remove previous build output for this configuration first.
    [switch] $Clean,

    # Run the test suite before publishing.
    [switch] $Test,

    # Trimmed + ReadyToRun instead of NativeAOT (no C++ toolchain required).
    [switch] $NoAot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'Klippy.Desktop\Klippy.Desktop.csproj'
$tests = Join-Path $root 'Klippy.Tests\Klippy.Tests.csproj'

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn([string] $Message) { Write-Host "    $Message" -ForegroundColor Yellow }

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

# --- preflight -------------------------------------------------------------

Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'
}
Write-Ok "dotnet SDK $(& dotnet --version)"

if (-not (Test-Path $project)) { throw "Cannot find $project - run this script from the repository." }

if (-not $NoAot) {
    # NativeAOT links with MSVC's linker. vswhere reports whether the workload component
    # is actually installed, which is cheaper and clearer than discovering it mid-build.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $component = if ($Runtime -eq 'win-arm64') {
        'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
    } else {
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    }

    $hasLinker = $false
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires $component -property installationPath 2>$null
        $hasLinker = -not [string]::IsNullOrWhiteSpace($found)
    }

    if (-not $hasLinker) {
        Write-Host ''
        Write-Host 'NativeAOT cannot link on this machine.' -ForegroundColor Red
        Write-Host ''
        Write-Host "  Missing Visual Studio component: $component"
        Write-Host ''

        # Prefer amending an existing install: adding one component is a fraction of the
        # size of a second, standalone Build Tools installation.
        $existing = if (Test-Path $vswhere) {
            & $vswhere -latest -products * -property installationPath 2>$null
        } else { $null }

        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            $setup = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\setup.exe'
            Write-Host '  Add it to your existing Visual Studio (elevated):'
            Write-Host "      & '$setup' modify --installPath '$existing' --add $component --quiet --norestart" -ForegroundColor Yellow
            Write-Host ''
            Write-Host '  Or: Visual Studio Installer > Modify > Individual components > search "MSVC".'
        } else {
            Write-Host '  Install the standalone C++ build tools:'
            Write-Host '      winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools"' -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host '  To build without it, use the trimmed + ReadyToRun fallback:'
        Write-Host '      .\build.ps1 -NoAot' -ForegroundColor Yellow
        Write-Host ''
        exit 1
    }
    Write-Ok "native linker present ($component)"

    # The ILC targets shell out to findvcvarsall.bat, which calls vcvarsall.bat. On
    # VS 2026 that script invokes `vswhere.exe` unqualified; if the VS Installer folder
    # is not on PATH the failure text goes to stderr, MSBuild captures stdout+stderr
    # together, and the noise ends up inside $(CppLinker) — producing a bogus link
    # command rather than an honest "not found" error. Putting vswhere on PATH for this
    # process keeps that output clean.
    $installerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path (Join-Path $installerDir 'vswhere.exe')) -and
        -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        $env:PATH = "$installerDir;$env:PATH"
        Write-Ok 'added VS Installer to PATH for this build (vswhere)'
    }
} else {
    Write-Warn 'NativeAOT disabled - building trimmed + ReadyToRun instead'
}

# --- clean -----------------------------------------------------------------

if ($Clean) {
    Write-Step 'Cleaning previous output'
    foreach ($dir in @('bin', 'obj')) {
        $path = Join-Path $root "Klippy.Desktop\$dir"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Ok "removed Klippy.Desktop\$dir" }
    }
}

# --- tests -----------------------------------------------------------------

if ($Test) {
    Write-Step 'Running tests'
    Invoke-Dotnet @('test', $tests, '-c', 'Release', '--nologo')
    Write-Ok 'tests passed'
}

# --- publish ---------------------------------------------------------------

Write-Step "Publishing $Runtime ($(if ($NoAot) { 'trimmed + ReadyToRun' } else { 'NativeAOT' }))"

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    '--nologo'
)

if ($NoAot) {
    $publishArgs += @('-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:PublishReadyToRun=true')
}
if ($Output) {
    $publishArgs += @('-o', $Output)
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
Invoke-Dotnet $publishArgs
$stopwatch.Stop()

# --- report ----------------------------------------------------------------

$publishDir = if ($Output) {
    $Output
} else {
    Join-Path $root "Klippy.Desktop\bin\Release\net10.0\$Runtime\publish"
}

Write-Step 'Result'
if (-not (Test-Path $publishDir)) { throw "Publish reported success but $publishDir does not exist." }

$exe = Join-Path $publishDir 'Klippy.Desktop.exe'
$files = Get-ChildItem $publishDir -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Ok "output   : $publishDir"
if (Test-Path $exe) {
    Write-Ok "exe      : Klippy.Desktop.exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
}
Write-Ok "total    : $totalMb MB across $($files.Count) file(s)"
Write-Ok "duration : $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"

if ($NoAot) {
    Write-Warn 'This is the fallback build. Install the C++ workload and re-run without -NoAot for a single small native binary.'
}
