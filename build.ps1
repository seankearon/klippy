<#
.SYNOPSIS
    Builds Klippy for Windows in Release with NativeAOT, or for Android with -Android.

.DESCRIPTION
    Publishes the desktop head as a self-contained NativeAOT binary: no JIT, fast cold
    start, and the smallest output the project can produce.

    NativeAOT needs a native linker from the Visual Studio "Desktop development with C++"
    workload. That is checked up front, because otherwise the failure arrives several
    minutes into the build as a bare "Platform linker not found".

    Pass -NoAot to fall back to trimmed + ReadyToRun, which needs no C++ toolchain and
    still starts quickly, at the cost of a much larger self-contained output.

    -Android builds the Android head instead. That is a wholly separate pipeline: it
    needs a JDK and the Android SDK rather than MSVC, and its "AOT" is Mono profiled
    AOT (native code for hot paths, with the Mono runtime still shipped in the APK)
    rather than NativeAOT. None of the MSVC preflight below applies to it.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Runtime win-arm64 -Clean -Test
    .\build.ps1 -NoAot          # no C++ toolchain available

    .\build.ps1 -Android                            # Release, profiled AOT, arm64
    .\build.ps1 -Android -Configuration Debug -Install   # quick build, push to device
    .\build.ps1 -Android -NoAot                     # skip AOT for faster iteration
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

    # Desktop: trimmed + ReadyToRun instead of NativeAOT (no C++ toolchain required).
    # Android: skips Mono AOT compilation, which is much faster to iterate on.
    [switch] $NoAot,

    # Build the Android head instead of the Windows desktop head.
    [switch] $Android,

    # Android ABI to publish. Real devices are arm64; the rest are for emulators.
    [ValidateSet('android-arm64', 'android-arm', 'android-x64', 'android-x86')]
    [string] $Abi = 'android-arm64',

    # Android configuration. Release turns on profiled AOT and full trimming.
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    # Android: install the resulting APK onto the connected device with adb.
    [switch] $Install
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

# --- Android ---------------------------------------------------------------

# Self-contained: the Android head shares nothing with the desktop path below except
# the .NET SDK check above, so it runs to completion and exits here.
if ($Android) {
    $androidProject = Join-Path $root 'Klippy.Android\Klippy.Android.csproj'
    if (-not (Test-Path $androidProject)) {
        throw "Cannot find $androidProject - run this script from the repository."
    }

    if (((& dotnet workload list) -join "`n") -notmatch '(?m)^android\s') {
        throw 'The android workload is missing. Install it with: dotnet workload install android'
    }
    Write-Ok 'android workload installed'

    # The Android SDK build tooling is Java-based; without a JDK the failure arrives
    # late and obscurely, so check it here alongside everything else.
    if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
        throw 'Building for Android needs a JDK (17+), but java is not on PATH.'
    }
    Write-Ok 'JDK on PATH'

    $sdk =
        if ($env:ANDROID_HOME) { $env:ANDROID_HOME }
        elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT }
        else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }

    if (-not (Test-Path $sdk)) {
        throw "Android SDK not found at '$sdk'. Set ANDROID_HOME, or install it via Android Studio."
    }
    Write-Ok "Android SDK $sdk"

    if ($Clean) {
        Write-Step 'Cleaning previous output'
        foreach ($dir in @('bin', 'obj')) {
            $path = Join-Path $root "Klippy.Android\$dir"
            if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Ok "removed Klippy.Android\$dir" }
        }
    }

    if ($Test) {
        Write-Step 'Running tests'
        Invoke-Dotnet @('test', $tests, '-c', 'Release', '--nologo')
        Write-Ok 'tests passed'
    }

    # Android packaging is incremental and gets it wrong: when an APK is already
    # present MSBuild will re-sign the previous package and report success with zero
    # errors, even though the assemblies have changed. A "successful" build then
    # silently ships stale code - verified by editing a source file, publishing, and
    # getting a byte-identical APK back. Deleting the packages first forces a real
    # repackage. It is much cheaper than -Clean because the AOT output in obj/ that
    # the deleted APKs were built from is still reused.
    Write-Step 'Removing stale packages'
    $stale = @(
        Get-ChildItem (Join-Path $root 'Klippy.Android\bin') -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $root 'Klippy.Android\obj') -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue
    )
    foreach ($package in $stale) { Remove-Item $package.FullName -Force }
    Write-Ok "removed $($stale.Count) previous package(s)"

    $aotLabel = if ($NoAot) { 'no AOT' } elseif ($Configuration -eq 'Release') { 'profiled AOT + full trim' } else { 'no AOT (Debug)' }
    Write-Step "Publishing $Abi $Configuration ($aotLabel)"

    $publishArgs = @(
        'publish', $androidProject,
        '-c', $Configuration,
        "-p:RuntimeIdentifier=$Abi",
        '--nologo'
    )
    # Clearing RunAOTCompilation alone is not enough: the csproj turns on profiled AOT
    # for Release, and Microsoft.Android.Sdk.Aot.targets imports the MonoAOTCompiler SDK
    # off the back of that, so the build fails at evaluation time when the AOT workload
    # pack is missing. Both flags have to go, along with the IL strip that follows AOT.
    if ($NoAot) {
        $publishArgs += @(
            '-p:RunAOTCompilation=false',
            '-p:AndroidEnableProfiledAot=false',
            '-p:AndroidStripILAfterAOT=false'
        )
    }
    if ($Output) { $publishArgs += @('-o', $Output) }

    $startedAt = Get-Date
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Dotnet $publishArgs
    $stopwatch.Stop()

    # --- report ------------------------------------------------------------

    $searchRoot = if ($Output) { $Output } else { Join-Path $root "Klippy.Android\bin\$Configuration" }
    $apk = Get-ChildItem $searchRoot -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    Write-Step 'Result'
    if (-not $apk) { throw "Publish reported success but no signed APK was found under $searchRoot." }

    # Belt and braces against the staleness trap above: if the APK predates this run,
    # the packaging step was skipped and the output cannot be trusted.
    if ($apk.LastWriteTime -lt $startedAt) {
        Write-Warn 'The APK is older than this build - packaging was skipped and it may contain stale code.'
        Write-Warn 'Re-run with -Clean.'
    }

    Write-Ok "apk      : $($apk.FullName)"
    Write-Ok "size     : $([math]::Round($apk.Length / 1MB, 1)) MB"
    Write-Ok "duration : $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"

    # Development builds sign with the Android SDK's shared debug key. The release
    # keystore lives outside the repo, in appbuild.env, and only release.ps1 reads it -
    # the one place that publishes an APK. MSBuild takes its signing properties from the
    # environment too, though, so a shell that has set them signs for real here as well.
    # Reported either way: the file name looks identical whichever key was used.
    if ($env:AndroidKeyStore -eq 'true' -and $env:AndroidSigningKeyStore) {
        Write-Ok "Signed with $($env:AndroidSigningKeyStore)"
    }
    else {
        Write-Warn 'Signed with the Android debug key - sideload only, and a later'
        Write-Warn 'release-signed build will refuse to install over it. Use release.ps1 to publish.'
    }

    if ($Install) {
        $adb = Join-Path $sdk 'platform-tools\adb.exe'
        if (-not (Test-Path $adb)) { throw "adb not found at $adb - install platform-tools." }

        Write-Step 'Installing'
        # -r reinstalls in place and keeps existing data.
        & $adb install -r $apk.FullName
        if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE" }
        Write-Ok 'installed'
    }

    exit 0
}

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
