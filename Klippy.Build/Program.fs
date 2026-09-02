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
let ParcelProject  = RepoFolder +/ "Klippy.Desktop" +/ "Klippy.Desktop.parcel"
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

// --- code signing ----------------------------------------------------------

/// Azure Trusted Signing (formerly Azure Code Signing), shared with Pirform.
///
/// Parcel does the signing itself - the app exe, the NSIS uninstaller and the installer,
/// all in the Package stage - using the account and certificate profile in the .parcel
/// file's Win32Settings. What it needs from here is a way to authenticate: it resolves
/// an Azure credential the standard way, so a service principal in the AZURE_* variables
/// of its environment is enough. These are Pirform's, from Pirform.Build's AzureSigning
/// module; the secret lives in the same environment variable that build reads.
///
/// The certificate is issued to the company behind the account, whichever profile is
/// used, so borrowing Pirform's profile signs Klippy as "Atlantic Business Solutions Ltd"
/// - which is the identity Windows checks, and the whole point.
///
/// Why sign at all: an unsigned NSIS installer wrapping a large native binary is exactly
/// the shape Defender's Wacatac.B!ml heuristic flags, and v1.0.2 was quarantined on
/// download. The bare exe scanned clean; the signed installer scans clean too.
module AzureSigning =
    let TenantId = "e661a696-2ab4-42d5-95fd-d876160f45a8"
    let ClientId = "fc97a9f9-e749-4afc-b165-2c8c4718f379"
    let SecretVariable = "PIRFORM_CODE_SIGNING_AZURE_CLIENT_SECRET"

    let secret () =
        Environment.GetEnvironmentVariable SecretVariable
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// Checked up front rather than left to Parcel, which would only fail after the
    /// NativeAOT publish it runs first - several minutes in, with an Azure error that
    /// says nothing about which variable was missing.
    let ensureCredentialsArePresent () =
        match secret () with
        | Some _ -> Write.line "Code-signing credentials present"
        | None ->
            failwith
                $"The code-signing secret is not set. Put the Entra app registration's client secret \
                  in the {SecretVariable} environment variable (it is the same one Pirform.Build uses)."

    /// Adds the service-principal credentials to a child process, and only there: the
    /// machine's environment is left alone.
    let addTo (si: ProcessStartInfo) =
        si.EnvironmentVariables["AZURE_TENANT_ID"] <- TenantId
        si.EnvironmentVariables["AZURE_CLIENT_ID"] <- ClientId
        si.EnvironmentVariables["AZURE_CLIENT_SECRET"] <- (secret () |> Option.defaultValue "")

// --- parcel ----------------------------------------------------------------

/// Runs Parcel with the signing credentials added and AVALONIA_TOOLS_LICENSE_KEY removed,
/// both in the child process only.
///
/// That variable holds a stale online key. Parcel prefers it over the saved portal
/// session and the portal then rejects it, so its presence turns a working setup into
/// "This subscription doesn't provide online license keys". Scrubbing it here fixes the
/// build without touching the machine's environment, which other tools also read.
let parcel (args: string list) =
    let configure (si: ProcessStartInfo) =
        si.EnvironmentVariables.Remove "AVALONIA_TOOLS_LICENSE_KEY"
        AzureSigning.addTo si

    cmdInRedirectingWith RepoFolder "parcel" (String.Join(" ", args)) configure true

// --- github ----------------------------------------------------------------

let gh = cmd "gh"

/// The installers, as opposed to Parcel's scratch files. Filtering by extension rather
/// than taking everything under the drop folder keeps temp/ and any stray logs out of a
/// published release.
let releaseArtifacts () =
    let installers = set [ ".exe"; ".dmg"; ".msix"; ".pkg"; ".zip"; ".deb"; ".rpm" ]

    Directory.GetFiles(DropFolder, "*", SearchOption.AllDirectories)
    |> Array.filter (fun f -> installers.Contains(Path.GetExtension(f).ToLowerInvariant()))
    |> Array.sort

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
                   $"The build expects to run on the {ReleaseBranch} branch, but is on {gitBranchName RepoFolder}."

            AzureSigning.ensureCredentialsArePresent ())

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
            //
            // Configuration must match too, and that is far less obvious. PublishAot is
            // set inside a Release-only PropertyGroup, and restore defaults to Debug - so
            // without this the ILCompiler package is never restored, and the Release
            // publish below quietly produces an ordinary self-contained build instead of
            // a native one. It succeeds; it just ships 103 MB rather than 30 MB.
            workingDir RepoFolder

            dotnet [
                "restore"; doubleQuote DesktopProject
                $"--runtime {WindowsRuntime}"
                "-p:Configuration=Release"
            ])

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
            ]

            // NativeAOT fails open, not closed: when the ILCompiler package is missing
            // from the assets file the publish still reports success and writes a managed
            // self-contained app. The apphost exe is the tell - a native publish produces
            // one, a managed publish here does not - so check for it rather than trust the
            // exit code.
            let publishDir = OutDir +/ WindowsRuntime
            let exe = publishDir +/ "Klippy.Desktop.exe"

            if not (File.Exists exe) then
                failwith
                    $"Publish reported success but {exe} does not exist - NativeAOT silently \
                      downgraded to a managed publish. Check that the Restore stage ran with \
                      -p:Configuration=Release so Microsoft.DotNet.ILCompiler is in the assets file."

            let files = Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories)

            // Excluding pdbs: the NativeAOT symbol file is larger than everything that
            // actually ships put together, so a raw total tells you nothing useful.
            let shippingMb =
                files
                |> Array.filter (fun f -> Path.GetExtension f <> ".pdb")
                |> Array.sumBy (fun f -> FileInfo(f).Length)
                |> fun bytes -> bytes / 1024L / 1024L

            Write.line $"Published {FileInfo(exe).Length / 1024L / 1024L} MB exe, {files.Length} file(s), {shippingMb} MB shipping"

            // A native publish is a handful of files. Anything resembling the ~220 of a
            // managed one means AOT did not really happen, however the exe check went.
            if files.Length > 50 then
                Write.line $"WARNING: {files.Length} files in a NativeAOT publish looks wrong - expected under a dozen.")

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
            let tag = $"v{version}"

            stage "Tag Repo" (fun () ->
                workingDir RepoFolder

                // git here is the non-failing runner, so a rejected tag would otherwise
                // pass silently and the release below would attach to whatever that tag
                // already pointed at - a previous build's commit.
                let existing = cmdInFolderReturningOutput RepoFolder "git" $"tag --list {tag}" |> trim

                if existing <> "" then
                    failwith $"Tag {tag} already exists. Bump ver.txt, or pass version:X.Y.Z for a different one."

                Write.line $"Tagging {ReleaseBranch} with {tag}"
                git $"tag {tag}"
                git $"push origin {tag}")

            stage "GitHub Release" (fun () ->
                workingDir RepoFolder

                let artifacts = releaseArtifacts ()

                if artifacts.Length = 0 then
                    failwith $"No installers under {DropFolder} - refusing to publish an empty release."

                Write.line $"Attaching {artifacts.Length} artifact(s):"
                for file in artifacts do
                    Write.line $"  {Path.GetFileName file} ({FileInfo(file).Length / 1024L / 1024L} MB)"

                // --generate-notes builds the changelog from the commits since the previous
                // tag, which is exactly the range this release covers.
                let title = doubleQuote $"Klippy {version}"

                gh [
                    "release"; "create"; tag
                    $"--title {title}"
                    "--generate-notes"
                    yield! artifacts |> Array.map doubleQuote
                ]

                Write.line $"Published release {tag}")

            stage "Update Version File" (fun () ->
                workingDir RepoFolder
                Write.line $"Updating the version file to {version}"
                writeFile VersionFile version
                git $"commit -m \"Updated by the build. [skip ci]\" \"{VersionFile}\""
                git $"push origin {ReleaseBranch}")
        else
            Write.line ""
            Write.line "Local build: skipped tagging, the GitHub release and the version bump."
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
