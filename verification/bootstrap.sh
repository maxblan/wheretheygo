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

have() { [ -x "$1" ]; }

# Reuse the survey toolchain if it is already present and working.
LEGACY="$HERE/../.verify-toolchain"
if have "$LEGACY/mmenv/bin/scip" && have "$LEGACY/vipr-build/viprchk"; then
    echo "bootstrap: reusing existing toolchain at $LEGACY"
    exit 0
fi

if have "$ENV/bin/scip" && have "$TC/vipr-build/viprchk"; then
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
    "$MAMBA" create -y -p "$ENV" -c conda-forge \
        "scip=$SCIP_PIN" gmp cmake "tbb-devel" "libboost-devel"
    version_line "$ENV/bin/scip" -c quit
fi

# Boost headers are needed by the vipr build; ensure they exist even when the
# env was created by an older bootstrap revision.
if [ ! -e "$ENV/include/boost/version.hpp" ]; then
    "$MAMBA" install -y -p "$ENV" -c conda-forge "libboost-devel"
fi

if ! have "$TC/vipr-build/viprchk"; then
    echo "bootstrap: building viprchk/viprttn"
    rm -rf "$TC/vipr" "$TC/vipr-build"
    git clone --depth 1 "$VIPR_REPO" "$TC/vipr"
    ( cd "$TC/vipr" && git rev-parse HEAD > "$TC/vipr-commit.txt" )
    mkdir -p "$TC/vipr-build"
    "$ENV/bin/cmake" -S "$TC/vipr/code" -B "$TC/vipr-build" \
        -DCMAKE_PREFIX_PATH="$ENV" -DCMAKE_BUILD_TYPE=Release
    "$ENV/bin/cmake" --build "$TC/vipr-build" -j
fi

echo "bootstrap: done"
version_line "$ENV/bin/scip" -c quit
version_line "$TC/vipr-build/viprchk"
