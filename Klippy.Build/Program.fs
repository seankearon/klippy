open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
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
let AndroidProject = RepoFolder +/ "Klippy.Android" +/ "Klippy.Android.csproj"
let TestProject    = RepoFolder +/ "Klippy.Tests"   +/ "Klippy.Tests.csproj"
let ParcelProject  = RepoFolder +/ "Klippy.Desktop" +/ "Klippy.Desktop.parcel"
let PropsFile      = RepoFolder +/ "Directory.Build.props"
let VersionFile    = RepoFolder +/ "ver.txt"
let BuildDir       = RepoFolder +/ "_build"
let OutDir         = BuildDir   +/ "out"
let DropFolder     = BuildDir   +/ "drop"

let ReleaseBranch = "main"
let WindowsRuntime = "win-x64"

/// The only ABI worth shipping: every current phone is arm64, and the others exist for
/// emulators. A second ABI would double the APK count for no one.
let AndroidAbi = "android-arm64"

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

// --- local configuration ---------------------------------------------------

/// The machine's private configuration: %USERPROFILE%\.config\shine.env, a KEY=value
/// file with # comments, shared by every Shine build and never checked in. Anything
/// here that identifies an Azure tenant, account or company belongs in that file, not
/// in this repo, which may one day be public.
///
/// Loaded into this process's environment (not the machine's), and only for keys that
/// are not already set - so a value exported in the shell still wins, which is how CI
/// or a one-off override would supply it. Child processes inherit the result, which is
/// what lets Parcel read its own settings with the env: prefix.
module ShineEnv =
    let Path =
        let home =
            Environment.GetEnvironmentVariable "USERPROFILE"
            |> Option.ofObj
            |> Option.defaultWith (fun () -> Environment.GetEnvironmentVariable "HOME")

        home +/ ".config" +/ "shine.env"

    let load () =
        if File.Exists Path then
            let mutable loaded = 0

            for raw in File.ReadAllLines Path do
                let line = raw.Trim()

                if line <> "" && not (line.StartsWith "#") then
                    match line.IndexOf '=' with
                    | i when i > 0 ->
                        let key = line.Substring(0, i).Trim()
                        let value = line.Substring(i + 1).Trim()

                        if String.IsNullOrEmpty(Environment.GetEnvironmentVariable key) then
                            Environment.SetEnvironmentVariable(key, value)
                            loaded <- loaded + 1
                    | _ -> ()

            Write.line $"Loaded {loaded} value(s) from {Path}"
        else
            Write.line $"No {Path} - relying on the environment alone"

// --- code signing ----------------------------------------------------------

/// Reads a configuration value from the environment, treating blank as absent.
let private setting name =
    Environment.GetEnvironmentVariable name
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

/// Developer ID signing and notarization for the macOS bundles.
///
/// The release runs on Windows, so the Keychain is not an option: Parcel signs with
/// rcodesign from a P12 export of the "Developer ID Application" certificate, and
/// notarizes with an Apple ID plus an app-specific password. All five values live in
/// shine.env beside the Azure ones and are injected into the copied .parcel project the
/// same way, for the same reason (Parcel's env: prefix is not reliable for these).
///
/// Optional, unlike Azure signing: with none of the keys set the bundles stay ad-hoc
/// signed, which runs on the build machine after a right-click > Open but shows every
/// other Mac Apple's "could not verify" dialog. A partial set is an error - it can only
/// be a typo, and finding out after the Windows packaging is the expensive way.
///
/// Whichever identity is used, SignDeep in the checked-in project is what matters for
/// the app to start at all: without it rcodesign re-signs the apphost alone, the .NET
/// runtime dylibs keep Microsoft's Team ID, and dyld refuses libhostfxr with "mapping
/// process and mapped file have different Team IDs" - a Dock bounce and no window.
/// Entitlements.plist beside the csproj does the rest: the hardened runtime that comes
/// with a Developer ID signature needs the CLR allowed to JIT.
module MacSigning =
    let P12Path     = "MacSigning__P12Path"
    let P12Password = "MacSigning__P12Password"
    let AppleId     = "MacSigning__AppleId"
    let TeamId      = "MacSigning__TeamId"
    let AppPassword = "MacSigning__AppPassword"

    let Required = [ P12Path; P12Password; AppleId; TeamId; AppPassword ]

    /// True when the release should be Developer ID signed and notarized.
    let isConfigured () = Required |> List.forall (setting >> Option.isSome)

    let ensureConfigurationIsCoherent () =
        match Required |> List.filter (setting >> Option.isNone) with
        | [] ->
            let p12 = setting P12Path |> Option.get

            if not (File.Exists p12) then
                failwith $"The macOS signing certificate {P12Path} points at {p12}, which does not exist."

            Write.line "macOS signing configuration present (Developer ID + notarization)"
        | missing when missing.Length = Required.Length ->
            Write.line "WARNING: no macOS signing configuration - the disk images will be ad-hoc signed."
        | missing ->
            failwith
                $"""macOS signing configuration is incomplete: {String.Join(", ", missing)} missing.
Set all of {String.Join(", ", Required)} in {ShineEnv.Path}, or none of them for an ad-hoc build."""

    /// Adds Developer ID signing and notarization to a Parcel project's MacOsSettings.
    /// Leaves the block alone when nothing is configured, so the ad-hoc defaults apply.
    let addTo (project: JsonObject) =
        if isConfigured () then
            let mac =
                match project["MacOsSettings"] with
                | null ->
                    let o = JsonObject()
                    project["MacOsSettings"] <- o
                    o
                | node -> node.AsObject()

            let value name = setting name |> Option.defaultValue ""
            mac["SigningCredentialsType"] <- JsonValue.Create "P12Certificate"
            mac["SigningP12Certificate"]  <- JsonValue.Create(Path.GetFullPath(value P12Path))
            mac["SigningP12Password"]     <- JsonValue.Create(value P12Password)
            mac["SignDeep"]               <- JsonValue.Create true
            mac["NotaryCredentialsType"]  <- JsonValue.Create "AppleAccount"
            mac["NotaryAppleId"]          <- JsonValue.Create(value AppleId)
            mac["TeamId"]                 <- JsonValue.Create(value TeamId)
            mac["NotaryAppPassword"]      <- JsonValue.Create(value AppPassword)
            Write.line "Added Developer ID signing and notarization to the macOS settings"

/// Azure Trusted Signing (formerly Azure Code Signing).
///
/// Parcel does the signing itself - the app exe, the NSIS uninstaller and the installer,
/// all in the Package stage - given a .parcel file whose Win32Settings name the endpoint,
/// account and certificate profile, and an Azure credential, which a service principal
/// in AZURE_* variables satisfies.
///
/// None of that is in the checked-in .parcel file, which knows nothing about signing.
/// Parcel's env: prefix would have been the obvious way to keep it out, but Parcel does
/// not resolve it for these settings (the literal "env:..." reaches signtool, which
/// fails with an internal error; the endpoint is rejected earlier still, as not a URL).
/// So the build writes a signed copy of the project under _build instead - the original
/// plus the signing block, with its relative paths made absolute so the copy works from
/// there - and packs from that. The repo stays clean, nothing needs reverting, and a
/// `parcel pack` on the checked-in file by hand still produces an unsigned build.
///
/// Why sign at all: an unsigned NSIS installer wrapping a large native binary is exactly
/// the shape Defender's Wacatac.B!ml heuristic flags, and v1.0.2 was quarantined on
/// download. The bare exe scanned clean; the signed installer scans clean too.
module AzureSigning =
    /// Every variable Parcel or the build reads. Named in the shine.env Section__Key style.
    let TenantId    = "CodeSigning__TenantId"
    let ClientId    = "CodeSigning__ClientId"
    let ClientSecret = "CodeSigning__ClientSecret"
    let Endpoint    = "CodeSigning__Endpoint"
    let AccountName = "CodeSigning__AccountName"
    let ProfileName = "CodeSigning__CertificateProfileName"

    let Required = [ TenantId; ClientId; ClientSecret; Endpoint; AccountName; ProfileName ]

    let get = setting

    /// Checked up front rather than left to Parcel, which would only fail after the
    /// NativeAOT publish it runs first - several minutes in, with an Azure error that
    /// says nothing about which variable was missing.
    let ensureCredentialsArePresent () =
        match Required |> List.filter (get >> Option.isNone) with
        | [] -> Write.line "Code-signing configuration present"
        | missing ->
            failwith
                $"""Code-signing configuration is missing: {String.Join(", ", missing)}.
Add them to {ShineEnv.Path} (tenant, client id and secret of the Entra app registration
that holds the Trusted Signing Certificate Profile Signer role; the endpoint, account
and certificate profile of the Trusted Signing resource)."""

    /// Adds the service-principal credentials to a child process, and only there.
    let addTo (si: ProcessStartInfo) =
        let value name = get name |> Option.defaultValue ""
        si.EnvironmentVariables["AZURE_TENANT_ID"]     <- value TenantId
        si.EnvironmentVariables["AZURE_CLIENT_ID"]     <- value ClientId
        si.EnvironmentVariables["AZURE_CLIENT_SECRET"] <- value ClientSecret

    /// Writes a copy of the .parcel project with the Trusted Signing block added (and
    /// the macOS Developer ID block, when MacSigning is configured) and returns its
    /// path. Paths in the project are relative to the file, so the copy, living
    /// elsewhere, gets them as absolute. Only the two that exist today are rewritten;
    /// Parcel would say soon enough if another appeared.
    let writeSignedParcelProject (source: string) (destination: string) =
        let sourceDir = Path.GetDirectoryName source
        let project = JsonNode.Parse(File.ReadAllText source).AsObject()

        let general = project["GeneralSettings"].AsObject()

        for key in [ "NetProjectPath"; "Icon" ] do
            match general[key] with
            | null -> ()
            | node -> general[key] <- JsonValue.Create(Path.GetFullPath(sourceDir +/ node.GetValue<string>()))

        let win32 =
            match project["Win32Settings"] with
            | null ->
                let o = JsonObject()
                project["Win32Settings"] <- o
                o
            | node -> node.AsObject()

        let value name = get name |> Option.defaultValue ""
        win32["SigningType"]                           <- JsonValue.Create "AzureTrustedSigning"
        win32["ArtifactSigningEndpoint"]               <- JsonValue.Create(value Endpoint)
        win32["ArtifactSigningCodeSigningAccountName"] <- JsonValue.Create(value AccountName)
        win32["ArtifactSigningCertificateProfileName"] <- JsonValue.Create(value ProfileName)

        MacSigning.addTo project

        ensureFolder (Path.GetDirectoryName destination) |> ignore
        File.WriteAllText(destination, project.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        Write.line $"Wrote signed Parcel project to {destination}"
        destination

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
    let installers = set [ ".exe"; ".dmg"; ".msix"; ".pkg"; ".zip"; ".deb"; ".rpm"; ".apk" ]

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

            ShineEnv.load ()
            AzureSigning.ensureCredentialsArePresent ()
            MacSigning.ensureConfigurationIsCoherent ())

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
            let signedProject = AzureSigning.writeSignedParcelProject ParcelProject (BuildDir +/ "Klippy.Desktop.parcel")

            // Parcel builds the app itself. That repeats the publish above for win-x64;
            // once the .parcel publish settings are confirmed to match the csproj
            // (AOT, trimming, self-contained), --no-build removes the duplication.
            parcel [
                "pack"; doubleQuote signedProject
                yield! runtimes |> List.map (fun r -> $"--runtimes {r}")
                "--packages nsis"
                "--packages dmg"
                $"--output {doubleQuote DropFolder}"
            ]

            let produced = Directory.GetFiles(DropFolder, "*", SearchOption.AllDirectories)
            Write.line $"Parcel produced {produced.Length} file(s) in {DropFolder}"
            for file in produced do
                Write.line $"  {Path.GetFileName file} ({FileInfo(file).Length / 1024L} KB)")

        stage "Publish Android" (fun () ->
            // Nothing to do with Parcel: the Android SDK packages and signs the APK itself,
            // so this is a plain publish whose output is copied into the drop folder under a
            // release name. Runs inside the Version stage's props like every other head, so
            // the APK carries the same version as the installers.
            workingDir RepoFolder

            let publishDir = OutDir +/ AndroidAbi

            dotnet [
                "publish"; doubleQuote AndroidProject
                "--configuration Release"
                $"-p:RuntimeIdentifier={AndroidAbi}"
                "--verbosity minimal"
                $"--output {publishDir |> doubleQuote}"

                // The escape hatch for the machine where the Mono AOT workload will not
                // resolve. Clearing RunAOTCompilation alone is not enough - the csproj turns
                // on profiled AOT for Release and the Android SDK imports the MonoAOTCompiler
                // SDK off the back of that, which fails at evaluation time. See build.ps1,
                // which carries the same three flags for the same reason.
                if hasArg "android-no-aot" then
                    "-p:RunAOTCompilation=false"
                    "-p:AndroidEnableProfiledAot=false"
                    "-p:AndroidStripILAfterAOT=false"
            ]

            // The unsigned APK sits beside this one; -Signed is the installable artifact.
            let apk =
                Directory.GetFiles(publishDir, "*-Signed.apk", SearchOption.AllDirectories)
                |> Array.sortByDescending (fun f -> FileInfo(f).LastWriteTimeUtc)
                |> Array.tryHead

            match apk with
            | None ->
                failwith
                    $"Publish reported success but no signed APK was found under {publishDir}. \
                      Android packaging is incremental and skips itself when an APK is already \
                      present and newer than its inputs."
            | Some source ->
                let target = DropFolder +/ $"Klippy.{AndroidAbi}.{version}.apk"
                copyFile source target
                Write.line $"Copied {Path.GetFileName source} to {Path.GetFileName target} ({FileInfo(target).Length / 1024L / 1024L} MB)"

                // Not a warning the build can act on, but one nobody should discover from a
                // user: with no keystore in the repo the Android SDK falls back to its shared
                // debug key, and a later properly-signed build will refuse to install over it.
                let hasKeystore =
                    File.ReadAllText(AndroidProject).Contains "AndroidSigningKeyStore"

                if not hasKeystore then
                    Write.line "WARNING: the APK is signed with the Android debug key (no keystore configured)."
                    Write.line "         It installs by sideloading, but is not fit for wider distribution.")

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
