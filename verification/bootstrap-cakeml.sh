#!/usr/bin/env bash
# Builds the FORMALLY VERIFIED VIPR checker `cake_vipr` from the CakeML sources.
# Optional third certificate check (after viprchk): its soundness is a HOL4
# theorem about the checker AND the compiler that produced the binary.
#
# There is no prebuilt binary anywhere (verified 2026-09-04: CakeML releases ship
# only the compiler; examples/vipr is HOL sources whose .S is a build product), so
# this needs PolyML + HOL4 + the CakeML dependency closure of examples/vipr —
# hours of CPU and 6-16 GB of RAM. Everything lands under verification/.toolchain/
# cakeml; no sudo. Idempotent: finished stages are skipped.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$HERE/.toolchain/cakeml"
export TMPDIR="$HERE/.toolchain/tmp"     # /tmp is noexec here
mkdir -p "$ROOT" "$TMPDIR"
export PREFIX="$ROOT/opt"
export PATH="$PREFIX/bin:$ROOT/HOL/bin:$PATH"
export HOLDIR="$ROOT/HOL"
JOBS="${JOBS:-6}"

log() { echo "[$(date -u +%H:%M:%S)] $*"; }

if [ ! -x "$PREFIX/bin/poly" ]; then
    log "stage 1/4: PolyML"
    [ -d "$ROOT/polyml" ] || git clone --depth 1 https://github.com/polyml/polyml "$ROOT/polyml"
    ( cd "$ROOT/polyml" && ./configure --prefix="$PREFIX" --enable-intinf-as-int \
        CPPFLAGS="-I$HERE/.toolchain/mmenv/include" LDFLAGS="-L$HERE/.toolchain/mmenv/lib -Wl,-rpath,$HERE/.toolchain/mmenv/lib" \
        && make -j"$JOBS" && make compiler && make install ) > "$ROOT/polyml.log" 2>&1
fi
log "poly: $(poly -v 2>&1 | head -1)"

if [ ! -x "$HOLDIR/bin/Holmake" ]; then
    log "stage 2/4: HOL4 (this is the long one)"
    [ -d "$HOLDIR" ] || git clone --depth 1 https://github.com/HOL-Theorem-Prover/HOL "$HOLDIR"
    ( cd "$HOLDIR" && poly --script tools/smart-configure.sml && bin/build ) > "$ROOT/hol.log" 2>&1
fi
log "Holmake: $(Holmake --version 2>&1 | head -1 || true)"

if [ ! -d "$ROOT/cakeml" ]; then
    log "stage 3/4: CakeML sources"
    git clone --depth 1 https://github.com/CakeML/cakeml "$ROOT/cakeml"
fi
export CAKEMLDIR="$ROOT/cakeml"

if [ ! -x "$ROOT/cakeml/examples/vipr/compilation/cake_vipr" ]; then
    log "stage 4/4: Holmake examples/vipr/compilation (dependency closure, hours)"
    ( cd "$ROOT/cakeml/examples/vipr/compilation" && Holmake -j"$JOBS" ) > "$ROOT/vipr.log" 2>&1
fi
cp "$ROOT/cakeml/examples/vipr/compilation/cake_vipr" "$HERE/.toolchain/bin/cake_vipr" 2>/dev/null || true
log "done: $ROOT/cakeml/examples/vipr/compilation/cake_vipr"
