# Editor command line

`ApogeeEditor` takes the same switches whether you launch it from a shortcut, a shell, CI or an
IDE debug configuration. Several of them make the editor do one job and exit, which is what turns
it into a build-server tool rather than only an application.

```bash
ApogeeEditor -project ../MyGame
ApogeeEditor -project ../MyGame -build Windows.Development -exit
ApogeeEditor -project ../MyGame -migrateassets
```

From a source checkout, `Build.sh`/`Build.bat` forwards everything after `--`:

```bash
./Build.sh run editor -- -project ../MyGame -exit
```

> [!NOTE]
> Switches are matched anywhere in the command line, case-insensitively, and each is recognised on
> its own — order does not matter. Ones that take a value expect it as the next argument
> (`-project ../MyGame`); quote it if it contains spaces.

## Choosing the project

The editor needs a project. Given none, it opens a file picker; in headless mode it exits with
`Missing project path.` instead, so CI always has to name one.

| Switch | Effect |
| --- | --- |
| `-project <path>` | Opens the project at this folder (or `.apogee` file). |
| `-lastproject` | Opens whatever project was open last. Ignored if `-project` is given. |
| `-new` | Scaffolds a new project in the given folder — `.apogee`, a `Source/Game` C# module and the standard directories — then opens it. |

## Run one job and exit

These do their work during startup and quit; the editor window never appears. Each returns a
non-zero exit code on failure, so a CI step can just check it.

| Switch | Effect |
| --- | --- |
| `-build <preset>` or `-build <preset.target>` | Cooks the game with that build preset and exits. |
| `-genprojectfiles` | Regenerates the scripts project files. |
| `-reimportshaders` | Regenerates `Content/Shaders/*.ashader` from `Source/Shaders/*.shader`. See [Rendering](../systems/rendering.md#shaders-and-materials). |
| `-migrateassets` | Renames every generic `.ap` asset under the engine and project content to the per-domain extension its type calls for. See [Content and assets](../systems/content.md#assets). |
| `-clearcache` | Clears the project cache folder. |
| `-clearcooker` | Clears the Game Cooker cache folder. |
| `-exit` | Exits once startup and every queued action has finished. Combine with anything above, or use alone as a "does this project still open?" smoke test. |

## Editor startup

| Switch | Effect |
| --- | --- |
| `-play [scene guid]` | Enters play mode on startup. Without a guid, plays the default scene. |
| `-skipcompile` | Skips compiling scripts at startup. Useful when launching from an IDE that has just built them. |
| `-shaderdebug` | Generates shader debug data and disables shader compiler optimizations. |
| `-shaderprofile` | Generates shader debug data but keeps optimizations on, for profiling representative code. |

## Graphics and window

Available to both the editor and a packaged game.

| Switch | Effect |
| --- | --- |
| `-vulkan`, `-d3d12`, `-d3d11`, `-d3d10` | Force a rendering backend, if the build supports it. |
| `-null` | Use the null rendering backend. No GPU work happens at all. |
| `-nvidia`, `-amd`, `-intel` | Hint which GPU to use on a multi-GPU machine. |
| `-windowed`, `-fullscreen` | Force the window mode. |
| `-vsync`, `-novsync` | Force vertical synchronization on or off. |
| `-lowdpi` | Disable High DPI awareness. |
| `-headless` | Run with no windows at all. Combine with `-null` and `-mute` for a machine with no display. |
| `-mute` | Disable audio playback and use the null audio backend. |

On Linux with the SDL platform, `-wayland` and `-x11` pick the display server to prefer.

## Diagnostics

| Switch | Effect |
| --- | --- |
| `-std` | Write the log to standard output as well as the log file — what you want in CI, where the log file is thrown away. |
| `-nolog` | Do not write a log file. |
| `-debug <ip:port>` | Attach address for the managed debugger. Not available in Release builds. |
| `-debugwait` | Wait up to five seconds for a debugger to attach before continuing. Not available in Release builds. |
| `-monolog` | Enable verbose Mono runtime diagnostics. |

## In CI

The three pieces that matter together: name the project, send the log somewhere visible, and make
sure the process ends.

```bash
ApogeeEditor -project ../MyGame -headless -null -mute -std -build Windows.Development -exit
```

The switches are parsed by `CommandLine::Parse` in `Source/Engine/Runtime/CommandLine.cpp`, which
is the authority if a switch here ever disagrees with the engine.
