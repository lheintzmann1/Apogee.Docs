# Building the engine

Apogee builds itself with its own build tool, `Apogee.Build`, driven through a thin per-platform
wrapper script at the repository root. There is no CMake step and no IDE dependency: the wrapper
is the supported entry point on every platform, and an IDE, if you want one, is generated from the
same data.

## Prerequisites

Every platform needs:

- **.NET SDK 8.** `Apogee.Build` is a .NET tool, and the engine's managed assembly targets .NET 8.
- **Git LFS.** The repository does not build without it — content and prebuilt binaries are LFS
  objects, and a checkout that has only the pointer files fails at the first asset it opens.

Then, per platform:

| Platform | Also needed |
| --- | --- |
| Linux | Vulkan SDK; `build-essential`, `gettext`, `libtool`, `libtool-bin`, `libx11-dev`, `libxcursor-dev`, `libxinerama-dev`, `libwayland-dev`, `libpulse-dev`, `libasound2-dev`, `libjack-dev`, `portaudio19-dev` |
| Windows | Visual Studio 2022 or newer with the MSVC toolset. The Vulkan SDK is optional — without it the Vulkan backend is skipped and only DirectX is built |
| macOS | Xcode command line tools |

The Vulkan SDK is located through the `VULKAN_SDK` environment variable. On Windows, set it
*before* generating project files, or the generated projects will still be missing the Vulkan
backend after you install the SDK.

## First checkout

```bash
git lfs install && git lfs pull
./Build.sh deps                 # unpack third-party SDKs, if you have them
./Build.sh generate --vscode    # IDE project files + C# bindings
./Build.sh                      # editor, Development, host platform
```

`Build.bat` takes the same arguments on Windows; `Build.command` is the macOS Finder entry point.
Both forward to the same tool.

### Third-party SDKs

FMOD, Steam Audio, Steamworks and DLSS are not redistributed with the engine. Download the
archives yourself, drop them in an `SDKs/` directory *next to* the repository, and run
`./Build.sh deps` to unpack them into
`Source/Platforms/<Platform>/Binaries/ThirdParty/<Arch>/`:

```text
SDKs/
  fmodstudioapi20xxxlinux.tar.gz
  steamaudio_4.x.x.zip
  steamworks_sdk_1xx.zip
Apogee.Engine/
```

The script skips anything it cannot find and reports what it did, so a partial `SDKs/` folder is
fine — you simply lose the corresponding feature. DLSS is Windows-only and needs no archive
elsewhere.

### Generated project files

Solutions and project files are not committed. They are a function of the `*.Build.cs` files and
are regenerated rather than merged, so generate the flavour you want:

```bash
./Build.sh generate --vscode
./Build.sh generate --rider
./Build.sh generate --vs2022     # or --vs2026
```

`generate` also builds the C# bindings for the editor target, so IntelliSense has something to
read immediately.

## Building

```bash
./Build.sh                      # editor, Development, host platform
./Build.sh editor -c Debug      # -c Debug | Development | Release
./Build.sh game -c Release
./Build.sh tests --run
./Build.sh bindings             # C# bindings only
./Build.sh package              # package the editor for distribution
./Build.sh clean
./Build.sh help
```

Options: `-c/--configuration`, `-a/--arch`, `-p/--platform`, `--dotnet`, `--rebuild`, `--run`,
`-v/--verbose`.

Everything after `--` is forwarded verbatim to `Apogee.Build`, so any switch the build tool
supports is reachable without editing the wrapper:

```bash
./Build.sh editor -- -printSDKs -perf
```

If you would rather drive the tool directly, the wrapper is only calling
`Development/Scripts/<Platform>/CallBuildTool.<sh|bat>`, which remains available.

### Configurations

| Configuration | What it is for |
| --- | --- |
| `Debug` | No optimization, full assertions. Slow, but the one to attach a debugger to. |
| `Development` | Optimized, with assertions and profiling still compiled in. The default, and what you want day to day. |
| `Release` | Fully optimized, with assertions and the crash reporter stripped. What you ship. |

### Targets

| Target | Output |
| --- | --- |
| `ApogeeEditor` | `Binaries/Editor/<Platform>/<Configuration>/` |
| `ApogeeGame` | `Source/Platforms/<Platform>/Binaries/Game/<Arch>/<Configuration>/` |
| `ApogeeTestsTarget` | `Binaries/Editor/<Platform>/Tests/` |

The tests target has an output folder of its own rather than sharing the editor's, because it is
the only target that compiles with `APOGEE_TESTS` defined. Sharing meant whichever of the two was
built last won, and the runner could silently end up testing an engine with no test support in it.

## Running

```bash
./Build.sh run editor
./Build.sh run editor -- -project ../MyGame
./Build.sh run tests
./Build.sh run game
```

For `run`, arguments after `--` go to the launched binary rather than to the build tool. The
native test runner needs none — it generates a temporary project for itself.

In Visual Studio, after `Build.bat generate --vs2022`: open `Apogee.sln`, select the
`Editor.Development` solution configuration and the `Win64` platform, and set `ApogeeEngine` as
the startup project.

## Shader assets

`Content/Shaders/*.ashader` is generated from `Source/Shaders/*.shader` by `Apogee.Build` and is not
committed. A shader asset is a container wrapping the source text under a deterministic id, so it
is a pure function of the `.shader` beside it — see
[Rendering](../systems/rendering.md#shaders-and-materials). The build regenerates it
automatically; `ApogeeEditor -reimportshaders` does only that and exits, which is occasionally
useful in CI — see [Editor command line](../editor/command-line.md) for the rest of the switches.

## Troubleshooting

| Message | Cause |
| --- | --- |
| `CallBuildTool ERROR: repository was not cloned with Git LFS` | Run `git lfs install && git lfs pull`. |
| `Could not execute because the specified command or file was not found.` | The .NET SDK is not on `PATH`. Reopen the shell after installing it. |
| `error NETSDK1045: The current .NET SDK does not support targeting .NET 8.0` | An older SDK is being picked up. Check `dotnet --info` against `global.json`. |
| `Building for Windows without Vulkan rendering backend (Vulkan SDK is missing)` | Install the Vulkan SDK, set `VULKAN_SDK`, and regenerate project files. |

Next: [Your first project](first-project.md).
