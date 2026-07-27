# Building the engine

Apogee builds through a thin per-platform wrapper over its own build tool, `Apogee.Build`.

```bash
./Build.sh                      # editor, Development, host platform
./Build.sh editor -c Debug      # -c Debug | Development | Release
./Build.sh game -c Release
./Build.sh bindings             # C# bindings only
./Build.sh generate --vscode    # IDE project files + bindings
./Build.sh clean
```

`Build.bat` takes the same arguments on Windows; `Build.command` is the macOS Finder entry point.
Everything after `--` is forwarded verbatim to `Apogee.Build`.

Two things a clean clone needs before any of this works:

- **Git LFS.** The build fails fast with a clear message if the checkout has only LFS pointers.
- **Generated project files.** Solutions and project files are not committed; run
  `./Build.sh generate --vscode` (or `--rider`, `--vs2022`) first.

> [!NOTE]
> Stub — expand with per-platform prerequisites and the dependency bootstrap
> (`GetDependencies.sh`).
