---
icon: lucide/hammer
description: "Building, publishing and packaging for desktop and Android."
---

# Building

```sh
dotnet run --project Klippy.Desktop            # run the desktop app
dotnet test                                    # run tests (also captures artifacts/screenshot-*.png)
dotnet build Klippy.Android -c Debug           # Android (requires android workload)
```

## Release / publish

On Windows use [`build.ps1`](build.ps1), which publishes the desktop head in Release with
**NativeAOT** — no JIT, fast cold start, smallest output:

```powershell
.\build.ps1                                  # NativeAOT, win-x64
.\build.ps1 -Runtime win-arm64 -Clean -Test  # arm64, clean first, run tests
.\build.ps1 -NoAot -Output C:\dist\klippy    # fallback, custom output folder
```

> **Enabling NativeAOT.** It needs the MSVC toolset — the Windows SDK alone is not
> enough. If Visual Studio is already installed, add the single component rather than
> installing a second, standalone Build Tools copy (run elevated, then restart the shell):
>
> ```powershell
> & 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe' modify `
>     --installPath 'C:\Program Files\Microsoft Visual Studio\18\Community' `
>     --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --quiet --norestart
> ```
>
> Add `Microsoft.VisualStudio.Component.VC.Tools.ARM64` as well to publish `win-arm64`.
> The equivalent in the GUI is Visual Studio Installer → Modify → Individual components →
> search "MSVC". `build.ps1` prints the command tailored to your machine.

> **Execution policy.** Windows PowerShell 5.1 refuses unsigned scripts by default. Use
> PowerShell 7 (`pwsh`), or run it as
> `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

> **Code signing (releases).** [`release.ps1`](release.ps1) hands packaging to Parcel,
> which signs the Windows exe, uninstaller and NSIS installer with **Azure Trusted
> Signing**. Nothing that identifies the signing account is in the repo: the build and
> the script load `%USERPROFILE%\.config\shine.env` (a private `KEY=value` file, never
> checked in) and refuse to start unless it holds all six keys —
> `CodeSigning__TenantId`, `CodeSigning__ClientId`, `CodeSigning__ClientSecret` for the
> Entra app registration that has the *Trusted Signing Certificate Profile Signer* role,
> and `CodeSigning__Endpoint`, `CodeSigning__AccountName`,
> `CodeSigning__CertificateProfileName` for the Trusted Signing resource. The checked-in
> `Klippy.Desktop.parcel` knows nothing about signing: the build writes a copy under
> `_build` with the signing block filled in from those keys and packs from that, so a
> hand-run `parcel pack` on the original still gives an unsigned build. A value already
> exported in the shell wins over the file. This exists because an unsigned NSIS
> installer wrapping a native binary trips Defender's `Wacatac.B!ml` heuristic: 1.0.2
> was quarantined on download. `build.ps1` is unaffected — it publishes the bare exe and
> signs nothing.

NativeAOT links with MSVC, so the script checks for the Visual Studio
"Desktop development with C++" workload **before** building. Without that check the
failure only surfaces minutes in, as a bare "Platform linker not found". When the
component is missing it names it, prints the winget command to install it, and points at
`-NoAot`.

`-NoAot` publishes trimmed + ReadyToRun instead: no C++ toolchain needed and still quick
to start, but self-contained and ~54 MB rather than a single small native binary.

Other platforms publish directly:

```sh
dotnet publish Klippy.Desktop -c Release -r osx-arm64    # macOS (run on a Mac)
```

iOS is AOT by nature and trims `SdkOnly` (build on a Mac).

## Android

`build.ps1 -Android` publishes the Android head. It needs a JDK (17+) and the Android
SDK — `ANDROID_HOME`, or the Android Studio default — and checks for both up front:

```powershell
.\build.ps1 -Android                                   # Release, profiled AOT, arm64
.\build.ps1 -Android -Configuration Debug -Install      # quick build, push to device
.\build.ps1 -Android -NoAot                            # skip AOT, much faster to iterate
.\build.ps1 -Android -Abi android-x64                  # emulator
```

`-Install` runs `adb install -r` against the connected device.

Android's AOT is **not** the desktop's NativeAOT. Release turns on Mono *profiled* AOT
(`RunAOTCompilation`, `AndroidEnableProfiledAot`) plus full trimming and
`AndroidStripILAfterAOT`: hot startup paths are precompiled to native code while the Mono
runtime still ships inside the APK. So none of the MSVC toolchain above is involved, and
the size trade runs the *other* way — Release is larger than Debug (~14 MB arm64-only vs
~10 MB), because the precompiled native code outweighs what stripping the IL gives back.
It buys startup time, not size. Use `-NoAot` while iterating.

> **Stale APKs.** Android packaging is incremental and gets it wrong: when an APK is
> already present, MSBuild re-signs the previous package and reports success with zero
> errors even though the assemblies changed — so a "successful" build can silently ship
> stale code. `build.ps1 -Android` deletes the previous packages first to force a real
> repackage (much cheaper than `-Clean`, since the AOT output in `obj/` is reused), and
> warns if the APK it produced predates the build. Running `dotnet publish` on the
> project by hand does **not** protect you from this.

> **Signing.** No keystore is configured, so APKs are signed with the shared Android
> debug key. That is fine for sideloading, but they are not distributable, and a later
> release-signed build will not install over one without uninstalling first.
> `ApplicationId` is also still the template's `com.CompanyName.Klippy`.
