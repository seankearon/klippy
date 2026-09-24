[<AutoOpen>]
module rec BuildLib

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text

// Adapted from a sibling project's BuildLib. Only the general-purpose parts came across:
// Klippy has no obfuscator and no InstallMate, and Parcel owns packaging and signing,
// so the signtool, MSIX and AssemblyInfo machinery was left behind. (Signing is still
// Azure Trusted Signing - see AzureSigning in Program.fs - but Parcel drives signtool
// itself; the build only loads the configuration from appbuild.env and lends it on.)
//
// Declarations are ordered so nothing refers forward. `module rec` is kept only so this
// file stays interchangeable with its counterpart — if the two are ever pulled into a
// shared repo, this one can be split into files without any reordering first.

let ApplicationExeFolder =
    Process.GetCurrentProcess().MainModule.FileName |> Path.GetDirectoryName

let stageHistory = Dictionary<string, TimeSpan>()

let (+/) path1 path2 = Path.Combine(path1, path2)

let doubleQuote s = $"\"{s}\""

let trim (s: string) = s.Trim()

/// The sibling project uses Humanizer for this one call. A build log needs "1m 04s", not
/// prose, so the dependency is not worth carrying.
let humanize (span: TimeSpan) =
    if span.TotalSeconds < 1.0 then $"%d{span.Milliseconds} ms"
    elif span.TotalMinutes < 1.0 then $"%.1f{span.TotalSeconds} seconds"
    elif span.TotalHours < 1.0 then $"%d{span.Minutes}m %02d{span.Seconds}s"
    else $"%d{int span.TotalHours}h %02d{span.Minutes}m"

module Write =
    let line (msg: string) = Console.WriteLine msg

    let blankLine () = line ""

    let separator () =
        line "====================================================================="

    let title (msg: string) =
        blankLine ()
        separator ()
        line msg
        separator ()

    let stageTitle (name: string) =
        blankLine ()
        separator ()
        line ("STAGE: " + name)
        separator ()

    let buildComplete timespan =
        let details =
            stageHistory
            |> Seq.mapi (fun i x ->
                {| number = i
                   name = x.Key
                   elapsed = x.Value |> humanize |})
            |> Seq.toList

        if details.IsEmpty then
            title "Build Complete"
            line $"Build completed in {timespan |> humanize}"
        else
            let nameMax = details |> List.map _.name.Length |> List.max
            let separatorLine = "".PadRight(nameMax + 20, '-')

            title "Build Complete"
            line separatorLine
            line "Stages"
            line separatorLine

            for detail in details do
                Console.WriteLine("{0,-2} {1} - {2}", detail.number, detail.name.PadRight nameMax, detail.elapsed)

            line separatorLine
            line $"Build completed in {timespan |> humanize}"
            line separatorLine

// --- files and folders -----------------------------------------------------

let parentFolder folder = Path.GetFullPath(folder +/ "..")

let useAltSeparator (s: string) = s.Replace('\\', Path.AltDirectorySeparatorChar)

let ensureFolder folder =
    let full = folder |> Path.GetFullPath
    full |> Directory.CreateDirectory |> ignore
    full

let deleteFile file =
    if File.Exists file then File.Delete file

let copyFile src dest = File.Copy(src, dest, true)

let copyFileToFolder folder (file: string) =
    ensureFolder folder |> ignore
    copyFile file (folder +/ Path.GetFileName file)

let copyFilesTo (targetFolder: string) (sourceFolder: string) =
    let source = Path.GetFullPath sourceFolder
    let target = Path.GetFullPath targetFolder |> ensureFolder

    Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
    |> Array.iter (fun file -> copyFile file (target +/ Path.GetFileName file))

/// Copies a folder and everything beneath it. copyFilesTo above is deliberately
/// top-level only, and a generated site is a tree.
let rec copyFolderTo (targetFolder: string) (sourceFolder: string) =
    let source = Path.GetFullPath sourceFolder
    let target = Path.GetFullPath targetFolder |> ensureFolder

    Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
    |> Array.iter (fun file -> copyFile file (target +/ Path.GetFileName file))

    Directory.GetDirectories source
    |> Array.iter (fun dir -> copyFolderTo (target +/ Path.GetFileName dir) dir)

/// Deletes a folder and everything beneath it. Read-only files are cleared first: the
/// docs stage runs `git init` in its drop folder, and git marks every loose object
/// read-only, which Directory.Delete then refuses to remove.
let clean (path: string) =
    if Directory.Exists path then
        for file in Directory.GetFiles(path, "*", SearchOption.AllDirectories) do
            let info = FileInfo file
            if info.IsReadOnly then info.IsReadOnly <- false

        Directory.Delete(path, recursive = true)

/// Writes text to a file, creating the folder if this is a first run. The sibling's
/// equivalent calls File.Delete first, which throws when the folder is absent.
let writeFile (path: string) (contents: string) =
    ensureFolder (Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)

let findFirstParentFolderContainingFile folder file =
    let rec loop folder =
        let folder = Path.GetFullPath folder

        if File.Exists(folder +/ file) then
            folder
        else
            let parent = parentFolder folder

            if parent = folder then
                failwithf "Could not find %s in any parent folder of %s" file folder
            else
                loop parent

    loop folder

// --- running commands ------------------------------------------------------

let startInfoFor (folder: string) (exe: string) (args: string) =
    let si = ProcessStartInfo()
    si.FileName <- exe
    si.Arguments <- args
    si.RedirectStandardOutput <- true
    si.RedirectStandardError <- true
    si.UseShellExecute <- false
    si.CreateNoWindow <- true
    si.WorkingDirectory <- folder

    Write.line $"Running command in {si.WorkingDirectory}"
    Write.line $"{si.FileName} {si.Arguments}"

    si

/// The general runner. <paramref name="configure"/> is the hook the Parcel stage uses to
/// scrub an environment variable for the child process only.
let cmdInRedirectingWith (folder: string) (exe: string) (args: string) (configure: ProcessStartInfo -> unit) failForNonZeroResults =
    let si = startInfoFor folder exe args
    si.StandardOutputEncoding <- Encoding.UTF8
    si.StandardErrorEncoding <- Encoding.UTF8
    configure si

    let p = Process.Start si

    // Drain both streams concurrently: a chatty publish can exceed the 64 KB pipe
    // buffer, which would deadlock a sequential ReadToEnd.
    let outTask = p.StandardOutput.ReadToEndAsync()
    let errTask = p.StandardError.ReadToEndAsync()
    p.WaitForExit()

    let output = outTask.Result
    let errors = errTask.Result

    if not (String.IsNullOrWhiteSpace output) then Write.line output
    if not (String.IsNullOrWhiteSpace errors) then Write.line errors

    if failForNonZeroResults && p.ExitCode <> 0 then
        failwith $"Exit code was non-zero ({p.ExitCode}) for command '{si.FileName} {si.Arguments}' in {si.WorkingDirectory}"

let cmdInRedirecting (folder: string) (exe: string) (args: string) failForNonZeroResults =
    cmdInRedirectingWith folder exe args ignore failForNonZeroResults

let cmdInFolderReturningOutput (folder: string) (exe: string) (args: string) =
    let si = startInfoFor folder exe args
    let p = Process.Start si
    let result = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    result

let cmd (exe: string) (args: string list) =
    cmdInRedirecting Environment.CurrentDirectory exe (String.Join(" ", args)) true

let cmdNoFail (exe: string) (args: string) =
    cmdInRedirecting Environment.CurrentDirectory exe args false

let git = cmdNoFail "git"
let dotnet = cmd "dotnet"

let workingDir dir = Environment.CurrentDirectory <- dir

// --- git -------------------------------------------------------------------

let gitHasNoPendingChanges folder =
    cmdInFolderReturningOutput folder "git" "status --porcelain=v1" |> String.IsNullOrWhiteSpace

let gitBranchName folder =
    cmdInFolderReturningOutput folder "git" "branch --show-current" |> trim

/// True when the branch has no commits on the remote that are not also local. Compares
/// against the already-fetched remote ref, so run a fetch first or this answers a stale
/// question.
let gitIsUpToDateWithRemote folder branch =
    let behind =
        cmdInFolderReturningOutput folder "git" $"rev-list --count HEAD..origin/{branch}" |> trim

    // An empty result means the remote ref is unknown (no upstream yet), which is not
    // the same as being behind - let the caller's message say so.
    behind = "0" || behind = ""

// --- versioning ------------------------------------------------------------

let readNextVersionFromFile file =
    let a = File.ReadAllText(file).Trim().Split '.' |> Array.map int32
    sprintf "%i.%i.%i" a[0] a[1] (a[2] + 1)

type BuildDetails = {
    Product: string
    Version: string
    Title: string
    Company: string
    Description: string
    Copyright: string
}

/// Klippy has no AssemblyInfo.cs files - every project is SDK-style and generates its
/// own assembly attributes, so adding them would be a CS0579 duplicate-attribute error.
/// A root Directory.Build.props is the equivalent, and it is what Parcel reads for the
/// bundle and installer metadata.
let createDirectoryBuildPropsFile (fullPath: string) (details: BuildDetails) =
    $"""<Project>
    <!-- Generated by Klippy.Build; edits here are overwritten and reverted by the build. -->
    <PropertyGroup>
        <Version>{details.Version}</Version>
        <InformationalVersion>{details.Version}</InformationalVersion>
        <FileVersion>{details.Version}</FileVersion>
        <Title>{details.Title}</Title>
        <Company>{details.Company}</Company>
        <Description>{details.Description}</Description>
        <Product>{details.Product}</Product>
        <Copyright>{details.Copyright}</Copyright>
    </PropertyGroup>
</Project>
"""
    |> writeFile fullPath

// --- stages ----------------------------------------------------------------

let runTimed (action: unit -> unit) =
    let stopwatch = Stopwatch.StartNew()
    action ()
    stopwatch.Stop()
    stopwatch.Elapsed

let stage (name: string) (action: unit -> unit) =
    Write.stageTitle name
    let time = runTimed action
    stageHistory[name] <- time
    Write.line $"Stage {name} completed in {time |> humanize}"

let verify (func: unit -> bool) (msg: string) =
    if not (func ()) then failwith msg
