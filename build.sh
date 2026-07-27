#!/usr/bin/env bash
#
# Builds the Apogee documentation site.
#
#   ./build.sh              full build: engine, C#, C++, Lua, site
#   ./build.sh site         rebuild only the site (manual edits; reuses existing API metadata)
#   ./build.sh api          regenerate all three API surfaces, without rebuilding the site
#   ./build.sh cpp|lua|cs   regenerate one API surface
#   ./build.sh engine       fetch/checkout the engine at the pinned revision only
#   ./build.sh serve        build the site and serve it on http://localhost:8080
#   ./build.sh clean        remove generated output
#
# The engine is not vendored here. It is checked out into ./Apogee.Engine at the revision in
# commit.txt, unless APOGEE_ENGINE points at an existing checkout — which is the usual case for
# local work, where ../Apogee.Engine is picked up automatically.

set -euo pipefail

cd "$(dirname "$0")"
ROOT="$(pwd)"

ENGINE_REPO="${APOGEE_ENGINE_REPO:-https://github.com/lheintzmann1/Apogee.Engine.git}"
ENGINE_DIR="${APOGEE_ENGINE:-}"
CONFIGURATION="${APOGEE_CONFIGURATION:-Development}"

info()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
fail()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 is required but not installed.${2:+ $2}"
}

# ---- Engine checkout ---------------------------------------------------------

resolve_engine() {
    if [ -n "$ENGINE_DIR" ]; then
        [ -d "$ENGINE_DIR" ] || fail "APOGEE_ENGINE is set to '$ENGINE_DIR', which does not exist."
        ENGINE_DIR="$(cd "$ENGINE_DIR" && pwd)"
        return
    fi

    # A sibling checkout is what a developer working on both repos will have.
    if [ -d "$ROOT/../Apogee.Engine/Source/Engine" ]; then
        ENGINE_DIR="$(cd "$ROOT/../Apogee.Engine" && pwd)"
        info "Using the sibling engine checkout at $ENGINE_DIR"
        warn "Building against the working tree, not the revision pinned in commit.txt."
        return
    fi

    ENGINE_DIR="$ROOT/Apogee.Engine"
}

fetch_engine() {
    resolve_engine

    # An engine supplied by the caller or found alongside is used as-is; only the checkout this
    # script owns gets reset to the pinned revision.
    if [ "$ENGINE_DIR" != "$ROOT/Apogee.Engine" ]; then
        link_engine
        return
    fi

    local commit
    commit="$(tr -d '[:space:]' < commit.txt)"
    [ -n "$commit" ] || fail "commit.txt is empty; it must hold the engine revision to document."

    require git
    if [ -d "$ENGINE_DIR/.git" ]; then
        info "Updating the engine checkout"
        git -C "$ENGINE_DIR" fetch --quiet origin
    else
        info "Cloning $ENGINE_REPO"
        git clone --quiet --filter=blob:none "$ENGINE_REPO" "$ENGINE_DIR"
    fi

    info "Checking out $commit"
    git -C "$ENGINE_DIR" -c advice.detachedHead=false checkout --quiet "$commit"

    # The engine keeps binary assets in LFS. Only the C# step needs real file contents, and it
    # needs them for the build to succeed at all, so pull them rather than leaving pointers.
    if git -C "$ENGINE_DIR" lfs version >/dev/null 2>&1; then
        git -C "$ENGINE_DIR" lfs pull || warn "git lfs pull failed; the C# build may not work."
    else
        warn "git-lfs is not installed; the engine checkout will contain LFS pointers."
    fi
}

# ---- API generation ----------------------------------------------------------

docgen() {
    require dotnet
    dotnet run --project tools/Apogee.DocGen/Apogee.DocGen.csproj -c Release -v q --property:WarningLevel=0 -- "$@"
}

build_cpp() {
    fetch_engine
    require doxygen "Install it from your package manager (e.g. 'sudo apt install doxygen')."

    info "Running Doxygen over the engine headers"
    mkdir -p obj/doxygen
    # INPUT is appended here because the engine path is only known at build time.
    {
        cat doxyfile
        printf '\nINPUT = "%s/Source/Engine" "%s/Source/Editor"\n' "$ENGINE_DIR" "$ENGINE_DIR"
        printf 'STRIP_FROM_PATH = "%s"\n' "$ENGINE_DIR"
    } > obj/doxyfile.generated
    doxygen obj/doxyfile.generated > /dev/null

    info "Converting Doxygen XML into DocFX pages"
    docgen cpp --config "$ROOT/docgen.json"
}

build_lua() {
    fetch_engine
    if [ ! -d obj/doxygen/xml ]; then
        warn "No Doxygen XML yet — run './build.sh cpp' first for fully typed Lua signatures."
    fi
    info "Extracting the Lua API from the sol2 bindings"
    docgen lua --config "$ROOT/docgen.json"
}

build_cs() {
    fetch_engine
    require dotnet

    local binaries
    binaries="$(find "$ENGINE_DIR/Binaries" -name 'Apogee.CSharp.dll' -path "*/$CONFIGURATION/*" 2>/dev/null | head -1 || true)"

    if [ -z "$binaries" ]; then
        info "Building the engine's C# bindings (needed for the C# API metadata)"
        [ -x "$ENGINE_DIR/Build.sh" ] || fail "Build.sh not found in '$ENGINE_DIR'."
        (cd "$ENGINE_DIR" && ./Build.sh bindings -c "$CONFIGURATION") \
            || fail "The engine bindings build failed; see the output above."
        binaries="$(find "$ENGINE_DIR/Binaries" -name 'Apogee.CSharp.dll' -path "*/$CONFIGURATION/*" 2>/dev/null | head -1 || true)"
        [ -n "$binaries" ] || fail "Apogee.CSharp.dll was not produced by the engine build."
    fi

    if [ ! -f "${binaries%.dll}.xml" ]; then
        warn "No Apogee.CSharp.xml next to the assembly — the C# API will have no descriptions."
    fi

    info "Extracting the C# API metadata (docfx metadata)"
    dotnet docfx metadata docfx.json
}

# docfx.json refers to the engine as ./Apogee.Engine. When the real checkout lives elsewhere
# (a sibling repo, or APOGEE_ENGINE), expose it there as a symlink rather than duplicating the
# path in configuration that CI and local builds would then have to disagree about.
link_engine() {
    if [ "$ENGINE_DIR" = "$ROOT/Apogee.Engine" ]; then
        return
    fi
    if [ -L "$ROOT/Apogee.Engine" ]; then
        rm -f "$ROOT/Apogee.Engine"
    elif [ -e "$ROOT/Apogee.Engine" ]; then
        fail "'$ROOT/Apogee.Engine' exists and is not a symlink; remove it or unset APOGEE_ENGINE."
    fi
    ln -s "$ENGINE_DIR" "$ROOT/Apogee.Engine"
}

build_site() {
    require dotnet
    info "Building the site"
    dotnet docfx build docfx.json
    for asset in favicon.ico logo.png; do
        [ -f "$asset" ] && cp "$asset" "_site/$asset"
    done
    info "Site written to _site/"
}

restore_tools() {
    require dotnet
    dotnet tool restore > /dev/null
}

# ---- Commands ----------------------------------------------------------------

case "${1:-all}" in
    all)
        restore_tools
        fetch_engine
        build_cpp
        build_lua
        build_cs
        build_site
        ;;
    engine)  fetch_engine ;;
    cpp)     restore_tools; build_cpp ;;
    lua)     restore_tools; build_lua ;;
    cs|csharp)
        restore_tools; build_cs ;;
    api)
        restore_tools
        build_cpp
        build_lua
        build_cs
        ;;
    site)
        restore_tools
        resolve_engine
        link_engine
        build_site
        ;;
    serve)
        restore_tools
        resolve_engine
        link_engine
        build_site
        info "Serving on http://localhost:8080 — Ctrl+C to stop"
        dotnet docfx serve _site --port 8080
        ;;
    clean)
        info "Removing generated output"
        rm -rf _site obj api api-cpp/*.yml api-lua/*.yml media/apogee.d.lua
        rm -rf tools/Apogee.DocGen/bin tools/Apogee.DocGen/obj
        ;;
    help|-h|--help)
        sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        fail "Unknown command '$1'. Run './build.sh help'."
        ;;
esac
