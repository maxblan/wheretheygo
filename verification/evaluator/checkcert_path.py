"""Independent shortest-path certificate checker.

Verifies, using only the instance data and the certificate (never the Dijkstra
that produced it):
  1. d[source] = 0.
  2. For every edge {a,b} with cost c: if d[a] is finite then d[b] is finite,
     d[b] <= d[a] + c, and d[a] <= d[b] + c.
     (Then for any path v0..vk from source: cost >= d[vk], by induction.)
  3. The claimed path is a real path from source to target over declared edges.
  4. Its exact cost equals d[target] equals the claimed cost.
Together: the path is a minimum-cost source→target path.

Exit code 0 iff the certificate verifies. Usable standalone:
    python3 -m evaluator.checkcert_path <instance.json> <certificate.json>
"""

from __future__ import annotations

import json
import sys
from fractions import Fraction

from common.canonical import bits_to_fraction, load_instance


def parse_label(text: str | None) -> Fraction | None:
    if text is None:
        return None
    num, _, den = text.partition("/")
    return Fraction(int(num), int(den))


def check_certificate(instance: dict, cert: dict) -> tuple[bool, str]:
    data = instance["data"]
    node_count = len(data["node_x_b32"])
    labels = [parse_label(t) for t in cert["labels"]]
    if len(labels) != node_count:
        return False, "label count mismatch"

    source = cert["source"]
    target = cert["target"]
    if labels[source] != Fraction(0):
        return False, "d[source] != 0"

    edges: dict[tuple[int, int], Fraction] = {}
    for a, b, cbits in zip(data["edge_a"], data["edge_b"], data["edge_cost_b32"]):
        c = bits_to_fraction(cbits)
        key = (min(a, b), max(a, b))
        if key not in edges or c < edges[key]:
            edges[key] = c
        da, db = labels[a], labels[b]
        for x, y in ((da, db), (db, da)):
            if x is not None:
                if y is None:
                    return False, f"edge ({a},{b}): finite label beside an infinite one"
                if y > x + c:
                    return False, f"edge ({a},{b}): triangle inequality violated"

    path = cert["path"]
    if not path or path[0] != source or path[-1] != target:
        return False, "path endpoints wrong"
    total = Fraction(0)
    for i in range(1, len(path)):
        key = (min(path[i - 1], path[i]), max(path[i - 1], path[i]))
        if key not in edges:
            return False, f"path hop {path[i - 1]}->{path[i]} is not an edge"
        total += edges[key]

    claimed = parse_label(cert["claimed_cost"])
    if labels[target] is None or claimed is None:
        return False, "target label or claimed cost missing"
    if total != claimed:
        return False, f"path cost {total} != claimed {claimed}"
    if labels[target] != claimed:
        return False, f"d[target] {labels[target]} != claimed {claimed}"
    return True, "certificate verified: path cost equals a proven lower bound"


def main() -> int:
    instance = load_instance(sys.argv[1])
    with open(sys.argv[2], "r", encoding="ascii") as f:
        cert = json.load(f)
    ok, why = check_certificate(instance, cert)
    print(("OK: " if ok else "FAIL: ") + why)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
