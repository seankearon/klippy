<#
.SYNOPSIS
    Publishes a Klippy release: builds the installers, tags the repo, and creates the
    GitHub release with the binaries attached.

.DESCRIPTION
    A front end for Klippy.Build, which does the work in stages and reports each one.
    This script adds the things worth having before an irreversible action: a check that
    the GitHub CLI can actually publish, a summary of what is about to happen, and a
    confirmation prompt.

    The gh check matters more than it looks. The build tags and pushes before it creates
    the release, so an unauthenticated gh would leave a tag pushed to origin with no
    release against it - a half-published state that has to be unpicked by hand. Checking
    first costs a second and avoids that entirely.

    Use build.ps1 for ordinary development builds. This script is only for releases.

    What it does, in order: verifies the tree is clean and on the release branch, pulls,
    runs the tests, publishes the Windows app with NativeAOT, packs the Windows installer
    and both macOS disk images with Parcel, tags the repo, creates the GitHub release, and
    bumps ver.txt.

.PARAMETER Version
    Pins the version instead of taking the next patch from ver.txt.

.PARAMETER DryRun
    Builds and packages, but does not tag, release or bump the version. Use this to check
    the installers before committing to a release.

.PARAMETER Force
    Skips the confirmation prompt. Intended for unattended use.

.EXAMPLE
    .\release.ps1
    Releases the next patch version after prompting for confirmation.

.EXAMPLE
    .\release.ps1 -DryRun
    Produces the installers in _build\drop without publishing anything.

.EXAMPLE
    .\release.ps1 -Version 1.2.0 -Force
    Releases 1.2.0 without prompting.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    # Build and package only: no tag, no GitHub release, no version bump.
    [switch] $DryRun,

    # Skip the confirmation prompt.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn([string] $Message) { Write-Host "    $Message" -ForegroundColor Yellow }

# --- preflight -------------------------------------------------------------

Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'
}
Write-Ok "dotnet SDK $(& dotnet --version)"

# Only needed for a real release; a dry run never talks to GitHub.
if (-not $DryRun) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI is not on PATH. Install it from https://cli.github.com, or use -DryRun.'
    }

    # gh auth status exits non-zero when logged out. Deliberately checked here rather
    # than left to the build, which would already have pushed the tag by then.
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The GitHub CLI is not authenticated. Run: gh auth login'
    }

    $account = (& gh api user --jq .login 2>$null)
    Write-Ok "gh authenticated$(if ($account) { " as $account" })"
}

# --- what is about to happen -----------------------------------------------

# Displayed so the prompt can name a version. Klippy.Build computes this itself and is
# the authority; this only mirrors its rule of bumping the patch component.
$versionFile = Join-Path $root 'ver.txt'

$plannedVersion =
    if ($Version) {
        $Version
    }
    elseif (Test-Path $versionFile) {
        $parts = (Get-Content $versionFile -Raw).Trim().Split('.')
        if ($parts.Count -ne 3) { throw "ver.txt should hold a three-part version, but contains '$((Get-Content $versionFile -Raw).Trim())'." }
        '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
    }
    else {
        throw "Cannot find $versionFile, and no -Version was given."
    }

Write-Step 'Release plan'
Write-Host "    version   : $plannedVersion"
Write-Host "    repository: $(& git -C $root remote get-url origin 2>$null)"
Write-Host "    branch    : $(& git -C $root branch --show-current 2>$null)"

if ($DryRun) {
    Write-Warn 'Dry run: installers only. Nothing will be tagged, released or pushed.'
}
else {
    Write-Host "    tag       : v$plannedVersion  (pushed to origin)"
    Write-Host "    release   : public GitHub release with the Windows installer and both macOS disk images"
    Write-Host ''
    Write-Warn 'The macOS disk images are ad-hoc signed: Gatekeeper will quarantine them,'
    Write-Warn 'and users will need right-click > Open the first time.'
}

# --- confirm ---------------------------------------------------------------

if (-not $DryRun -and -not $Force) {
    Write-Host ''
    # $() around the variable is required, not stylistic: '?' is a legal character in a
    # PowerShell variable name, so "v$plannedVersion?" parses as the variable
    # $plannedVersion? and fails under Set-StrictMode.
    $answer = Read-Host "Publish release v$($plannedVersion)? [y/N]"

    if ($answer -notmatch '^(y|yes)$') {
        Write-Warn 'Cancelled. Nothing has been changed.'
        exit 1
    }
}

# --- run the build ---------------------------------------------------------

$buildArgs = @('run', '--project', (Join-Path $root 'Klippy.Build'), '-c', 'Release', '--')

if (-not $DryRun) { $buildArgs += 'release' }
if ($Version) { $buildArgs += "version:$Version" }

Write-Step "Running the build$(if ($DryRun) { ' (dry run)' })"
Write-Host "    dotnet $($buildArgs -join ' ')" -ForegroundColor DarkGray

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet @buildArgs
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

# --- report ----------------------------------------------------------------

Write-Step 'Result'

if ($exitCode -ne 0) {
    Write-Host "    The build failed with exit code $exitCode." -ForegroundColor Red

    # Worth saying explicitly: the build reverts its own generated files, but a failure
    # after the tag stage leaves the tag behind, and that has to be cleared by hand.
    if (-not $DryRun) {
        Write-Warn "If it failed after tagging, remove the tag before retrying:"
        Write-Warn "    git tag -d v$plannedVersion; git push origin :refs/tags/v$plannedVersion"
    }

    exit $exitCode
}

$drop = Join-Path $root '_build\drop'
if (Test-Path $drop) {
    $artifacts = Get-ChildItem $drop -Recurse -File -Include '*.exe', '*.dmg', '*.msix', '*.pkg', '*.zip'
    foreach ($file in $artifacts) {
        Write-Ok "$($file.Name) ($([math]::Round($file.Length / 1MB, 1)) MB)"
    }
}

Write-Ok "duration : $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"

if ($DryRun) {
    Write-Host ''
    Write-Warn "Dry run complete - nothing was published. Re-run without -DryRun to release."
}
else {
    $url = (& gh release view "v$plannedVersion" --json url --jq .url 2>$null)
    if ($url) { Write-Ok "release  : $url" }
}
