#!/usr/bin/env bash
# Installs the pinned Lean 4 toolchain project-locally (verification/.toolchain/elan)
# and builds the verified checker. Optional: the pipeline runs without it and
# reports the formally verified check as skipped. ~300 MB download; no sudo.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
export ELAN_HOME="$HERE/.toolchain/elan"
export TMPDIR="$HERE/.toolchain/tmp"
mkdir -p "$TMPDIR"
TOOLCHAIN="$(cat "$HERE/lean/lean-toolchain")"

if [ ! -x "$ELAN_HOME/bin/elan" ]; then
    echo "bootstrap-lean: installing elan"
    curl -sSfL https://raw.githubusercontent.com/leanprover/elan/master/elan-init.sh \
        -o "$TMPDIR/elan-init.sh"
    sh "$TMPDIR/elan-init.sh" -y --no-modify-path --default-toolchain none
fi

export PATH="$ELAN_HOME/bin:$PATH"
elan toolchain install "$TOOLCHAIN" 2>/dev/null || true

cd "$HERE/lean"
lake build Verify verify
echo "bootstrap-lean: done — checker at lean/.lake/build/bin/verify"
