#!/usr/bin/env bash
# Installs the project-local verification toolchain. Idempotent, no sudo, nothing
# global: everything lands in verification/.toolchain/. Pinned versions — see
# docs/verification-architecture.md.
#
# Notes for this environment (verified 2026-09-03):
#  - /tmp may be mounted noexec: builds run with TMPDIR under the repo.
#  - The PyPI PySCIPOpt wheel bundles a SCIP WITHOUT exact mode; the conda-forge
#    scip 10.0.1 package HAS it. Only the conda binary is used here.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
TC="$HERE/.toolchain"
MAMBA="$TC/bin/micromamba"
ENV="$TC/mmenv"
export TMPDIR="$TC/tmp"
mkdir -p "$TC/bin" "$TMPDIR"

SCIP_PIN="10.0.1"
VIPR_REPO="https://github.com/scipopt/vipr.git"
VIPR_COMMIT="30f2951d1e90e47afa821bdd1b12b82246656c42"

have() { [ -x "$1" ]; }

have_pinned_vipr() {
    local base="$1"
    have "$base/vipr-build/viprchk" \
        && have "$base/vipr-build/viprcomp" \
        && [ -r "$base/vipr-commit.txt" ] \
        && [ "$(cat "$base/vipr-commit.txt")" = "$VIPR_COMMIT" ]
}

# Reuse the survey toolchain if it is already present and working.
LEGACY="$HERE/../.verify-toolchain"
if have "$LEGACY/mmenv/bin/scip" && have_pinned_vipr "$LEGACY"; then
    echo "bootstrap: reusing existing toolchain at $LEGACY"
    exit 0
fi

if have "$ENV/bin/scip" && have_pinned_vipr "$TC"; then
    echo "bootstrap: toolchain already installed at $TC"
    exit 0
fi

if ! have "$MAMBA"; then
    echo "bootstrap: fetching micromamba"
    curl -Ls https://micro.mamba.pm/api/micromamba/linux-64/latest \
        | tar -xj -C "$TC" bin/micromamba
fi

version_line() {
    # Never `cmd | head` here: under pipefail, head's early close kills the
    # producer with SIGPIPE and aborts the whole script mid-way.
    "$@" > "$TMPDIR/version.txt" 2>&1 || true
    head -1 "$TMPDIR/version.txt"
}

if ! have "$ENV/bin/scip"; then
    echo "bootstrap: installing SCIP $SCIP_PIN (exact mode) from conda-forge"
    # soplex/gfortran/zlib/cxx-compiler are for viprcomp, not for SCIP: SCIP 10
    # writes VIPR 1.1 "weak" derivations that viprchk cannot parse on its own, and
    # viprcomp (which completes them) only builds when SoPlex is found. gfortran is
    # pulled in by SoPlex's PaPILO config; the conda compilers are needed because
    # that config hands the system linker sysroot paths (/lib64/...) that do not
    # exist on Debian-family layouts.
    "$MAMBA" create -y -p "$ENV" -c conda-forge \
        "scip=$SCIP_PIN" gmp cmake "tbb-devel" "libboost-devel" \
        soplex gfortran zlib cxx-compiler
    version_line "$ENV/bin/scip" -c quit
fi

# Boost headers are needed by the vipr build; ensure they exist even when the
# env was created by an older bootstrap revision.
if [ ! -e "$ENV/include/boost/version.hpp" ]; then
    "$MAMBA" install -y -p "$ENV" -c conda-forge "libboost-devel"
fi

# Upgrade toolchains created by older bootstrap revisions. Without these
# packages CMake silently omits viprcomp, leaving non-trivial SCIP 10
# certificates unverifiable.
if ! have "$TC/vipr-build/viprcomp"; then
    "$MAMBA" install -y -p "$ENV" -c conda-forge \
        soplex gfortran zlib cxx-compiler
fi

if ! have_pinned_vipr "$TC"; then
    echo "bootstrap: building viprchk/viprttn/viprcomp"
    rm -rf "$TC/vipr" "$TC/vipr-build"
    git init "$TC/vipr"
    git -C "$TC/vipr" remote add origin "$VIPR_REPO"
    git -C "$TC/vipr" fetch --depth 1 origin "$VIPR_COMMIT"
    git -C "$TC/vipr" checkout --detach "$VIPR_COMMIT"
    ( cd "$TC/vipr" && git rev-parse HEAD > "$TC/vipr-commit.txt" )
    mkdir -p "$TC/vipr-build"
    CC="$ENV/bin/x86_64-conda-linux-gnu-gcc" \
    CXX="$ENV/bin/x86_64-conda-linux-gnu-g++" \
    PATH="$ENV/bin:$PATH" \
        "$ENV/bin/cmake" -S "$TC/vipr/code" -B "$TC/vipr-build" \
            -DCMAKE_PREFIX_PATH="$ENV" -DCMAKE_BUILD_TYPE=Release
    PATH="$ENV/bin:$PATH" "$ENV/bin/cmake" --build "$TC/vipr-build" -j
fi

if ! have_pinned_vipr "$TC"; then
    echo "bootstrap: ERROR — the pinned VIPR checker/completer is unavailable." >&2
    echo "Expected commit $VIPR_COMMIT with viprchk and viprcomp." >&2
    exit 1
fi

echo "bootstrap: done"
version_line "$ENV/bin/scip" -c quit
version_line "$TC/vipr-build/viprchk"
