open System
open System.Diagnostics
open System.IO
open BuildLib

// Klippy's release build, modelled on Pirform.Build: a sequence of named stages, each
// timed and reported, with the whole run summarised at the end.
//
// Run it with:
//     dotnet run --project Klippy.Build
//     dotnet run --project Klippy.Build -- version:1.2.0
//     dotnet run --project Klippy.Build -- release
//
// Without `release` nothing leaves the machine: no tag, no push, no version bump. That
// is deliberate for a build this new - see the Release stages at the bottom.

let RepoFolder = findFirstParentFolderContainingFile ApplicationExeFolder "Klippy.slnx"

let DesktopProject = RepoFolder +/ "Klippy.Desktop" +/ "Klippy.Desktop.csproj"
let TestProject    = RepoFolder +/ "Klippy.Tests"   +/ "Klippy.Tests.csproj"
let ParcelProject  = RepoFolder +/ "Klippy.Desktop" +/ "Klippy.parcel"
let PropsFile      = RepoFolder +/ "Directory.Build.props"
let VersionFile    = RepoFolder +/ "ver.txt"
let BuildDir       = RepoFolder +/ "_build"
let OutDir         = BuildDir   +/ "out"
let DropFolder     = BuildDir   +/ "drop"

let ReleaseBranch = "main"
let WindowsRuntime = "win-x64"

/// Both mac architectures: Parcel merges them into one universal bundle with lipo, so a
/// single .dmg runs natively on Apple Silicon and Intel alike.
let MacRuntimes = [ "osx-arm64"; "osx-x64" ]

// --- arguments -------------------------------------------------------------

let mutable applicationArgs: string array = [||]

let hasArg name =
    applicationArgs |> Array.exists (fun a -> String.Equals(a, name, StringComparison.OrdinalIgnoreCase))

/// `version:1.2.0` pins the version instead of taking the next one from ver.txt.
let versionOverride =
    lazy
        (applicationArgs
         |> Array.tryPick (fun a ->
             if a.StartsWith("version:", StringComparison.OrdinalIgnoreCase) then
                 Some(a.Substring(8) |> trim)
             else
                 None))

// --- preflight -------------------------------------------------------------

/// NativeAOT links with MSVC's linker, and the ILC targets reach it through
/// vcvarsall.bat, which on VS 2026 calls `vswhere.exe` unqualified. When the VS Installer
/// folder is not on PATH, that failure goes to stderr, MSBuild folds stdout and stderr
/// together, and the noise lands inside $(CppLinker) - so the link command becomes
/// "'vswhere.exe' is not recognized...;...link.exe" and fails with exit 123. It reads
/// like a missing linker and is not. build.ps1 does the same thing for the same reason.
let ensureNativeLinkerIsReachable () =
    let installerDir =
        Environment.GetEnvironmentVariable "ProgramFiles(x86)"
        +/ "Microsoft Visual Studio"
        +/ "Installer"

    let vswhere = installerDir +/ "vswhere.exe"

    if not (File.Exists vswhere) then
        failwith
            $"vswhere.exe not found at {vswhere}. NativeAOT needs the MSVC toolchain: install the \
              'Desktop development with C++' workload, or the Microsoft.VisualStudio.Component.VC.Tools.x86.x64 component."

    let component' = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"

    let found =
        cmdInFolderReturningOutput RepoFolder vswhere $"-latest -products * -requires {component'} -property installationPath"
        |> trim

    if String.IsNullOrWhiteSpace found then
        failwith
            $"The MSVC native linker is missing (no install provides {component'}).\n\
              Add it via the Visual Studio Installer > Modify > Individual components > search 'MSVC'."

    Write.line $"Native linker present ({component'})"

    // Put vswhere on PATH for this process; child dotnet/Parcel builds inherit it.
    let path = Environment.GetEnvironmentVariable "PATH"

    if not (path.Contains(installerDir, StringComparison.OrdinalIgnoreCase)) then
        Environment.SetEnvironmentVariable("PATH", $"{installerDir};{path}")
        Write.line "Added the VS Installer folder to PATH for this build (vswhere)"

// --- parcel ----------------------------------------------------------------

/// Runs Parcel with AVALONIA_TOOLS_LICENSE_KEY removed from the child process only.
///
/// That variable holds a stale online key. Parcel prefers it over the saved portal
/// session and the portal then rejects it, so its presence turns a working setup into
/// "This subscription doesn't provide online license keys". Scrubbing it here fixes the
/// build without touching the machine's environment, which other tools also read.
let parcel (args: string list) =
    let scrubLicenceKey (si: ProcessStartInfo) =
        si.EnvironmentVariables.Remove "AVALONIA_TOOLS_LICENSE_KEY"

    cmdInRedirectingWith RepoFolder "parcel" (String.Join(" ", args)) scrubLicenceKey true

// --- the build -------------------------------------------------------------

let buildKlippy () =
    let stopwatch = Stopwatch.StartNew()
    let isRelease = hasArg "release"

    // Read before anything is modified, so the failure handler below knows whether the
    // props file it reverts was ours to begin with.
    let propsExistedBefore = File.Exists PropsFile

    let revertPropsFile () =
        if propsExistedBefore then
            git $"checkout -- \"{PropsFile}\""
            Write.line "Reverted Directory.Build.props"
        elif File.Exists PropsFile then
            deleteFile PropsFile
            Write.line "Removed the generated Directory.Build.props"

    try
        stage "Verify" (fun () ->
            verify (fun () -> gitHasNoPendingChanges RepoFolder)
                   "Cannot run the build: there are uncommitted changes."

            verify (fun () -> gitBranchName RepoFolder = ReleaseBranch)
                   $"The build expects to run on the {ReleaseBranch} branch, but is on {gitBranchName RepoFolder}.")

        stage "Update" (fun () ->
            workingDir RepoFolder

            // --ff-only: an automated build must never invent a merge commit. If the
            // branch has diverged this stops here rather than producing a release from
            // a tree nobody has seen.
            git "pull --ff-only"

            verify (fun () -> gitIsUpToDateWithRemote RepoFolder ReleaseBranch)
                   $"{ReleaseBranch} is still behind origin after pulling - resolve that before releasing.")

        stage "Clean" (fun () ->
            clean BuildDir
            ensureFolder BuildDir |> ignore
            ensureFolder DropFolder |> ignore)

        stage "Restore" (fun () ->
            // The runtime must match the publish stage below: with --no-restore there,
            // win-x64 has to already be in the assets file from this restore.
            workingDir RepoFolder
            dotnet [ "restore"; doubleQuote DesktopProject; $"--runtime {WindowsRuntime}" ])

        stage "Test" (fun () ->
            workingDir RepoFolder
            dotnet [ "test"; doubleQuote TestProject; "--configuration Release"; "--nologo" ])

        let version =
            match versionOverride.Value with
            | Some v -> v
            | None -> readNextVersionFromFile VersionFile

        stage "Version" (fun () ->
            Write.line $"Building version {version}"

            createDirectoryBuildPropsFile PropsFile {
                Product     = "Klippy"
                Version     = version
                Title       = "Klippy"
                Company     = "Sean Kearon"
                Description = "Snippet launcher and clipboard history."
                Copyright   = $"Sean Kearon {DateTime.Now.Year}"
            }

            Write.line $"Wrote {PropsFile}")

        stage "Publish Windows" (fun () ->
            // Independently useful: this is the runnable NativeAOT exe, produced whether
            // or not Parcel can be reached in the stage below.
            workingDir RepoFolder

            dotnet [
                "publish"; doubleQuote DesktopProject
                "--no-restore"
                "--configuration Release"
                $"--runtime {WindowsRuntime}"
                "--self-contained true"
                "--verbosity minimal"
                $"--output {OutDir +/ WindowsRuntime |> doubleQuote}"
            ])

        stage "Package" (fun () ->
            if not (File.Exists ParcelProject) then
                failwith
                    $"No Parcel project at {ParcelProject}.\n\
                      Create it with the Parcel MCP's create-project tool (it is MCP-only; the CLI \
                      exposes only pack/step/install-tools), then re-run."

            let runtimes = WindowsRuntime :: MacRuntimes

            // Parcel builds the app itself. That repeats the publish above for win-x64;
            // once the .parcel publish settings are confirmed to match the csproj
            // (AOT, trimming, self-contained), --no-build removes the duplication.
            parcel [
                "pack"; doubleQuote ParcelProject
                yield! runtimes |> List.map (fun r -> $"--runtimes {r}")
                "--packages nsis"
                "--packages dmg"
                $"--output {doubleQuote DropFolder}"
            ]

            let produced = Directory.GetFiles(DropFolder, "*", SearchOption.AllDirectories)
            Write.line $"Parcel produced {produced.Length} file(s) in {DropFolder}"
            for file in produced do
                Write.line $"  {Path.GetFileName file} ({FileInfo(file).Length / 1024L} KB)")

        stage "Revert Generated Files" (fun () ->
            workingDir RepoFolder
            revertPropsFile ())

        if isRelease then
            stage "Tag Repo" (fun () ->
                workingDir RepoFolder
                let tag = $"v{version}"
                Write.line $"Tagging {ReleaseBranch} with {tag}"
                git $"tag {tag}"
                git $"push origin {tag}")

            stage "Update Version File" (fun () ->
                workingDir RepoFolder
                Write.line $"Updating the version file to {version}"
                writeFile VersionFile version
                git $"commit -m \"Updated by the build. [skip ci]\" \"{VersionFile}\""
                git $"push origin {ReleaseBranch}")
        else
            Write.line ""
            Write.line "Local build: skipped tagging, the version bump and the push."
            Write.line "Re-run with `release` to publish."

        stopwatch.Stop()
        Write.buildComplete stopwatch.Elapsed
        0

    with ex ->
        Write.stageTitle "FAILED"

        try
            workingDir RepoFolder
            revertPropsFile ()
        with cleanupError ->
            Write.line $"Could not revert Directory.Build.props: {cleanupError.Message}"

        Write.line "########### FAILED ###########"
        Write.line $"Error: {ex.Message}"
        Write.line "########### END ###########"
        1

[<EntryPoint>]
let main argv =
    applicationArgs <- argv

    ensureNativeLinkerIsReachable ()
    buildKlippy ()
