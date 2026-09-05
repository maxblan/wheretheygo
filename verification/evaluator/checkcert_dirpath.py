"""Independent (Python) check of a DIRECTED shortest-path certificate over an
explicit edge list: d[source] = 0, every directed edge (a, b, c) satisfies
d[b] <= d[a] + c whenever d[a] is finite, the path is a chain of listed edges
from source to target, and its cost equals d[target] equals the claim. The Lean
checker (Verify.DirPathCert.check) re-checks the same certificate."""

from __future__ import annotations

from fractions import Fraction


def parse(text: str | None) -> Fraction | None:
    if text is None:
        return None
    num, _, den = text.partition("/")
    return Fraction(int(num), int(den))


def check_directed_certificate(cert: dict) -> tuple[bool, str]:
    labels = [parse(t) for t in cert["labels"]]
    source, target = cert["source"], cert["target"]
    if source >= len(labels) or labels[source] != 0:
        return False, "d[source] != 0"
    first: dict[tuple[int, int], Fraction] = {}
    for a, b, c in cert["edges"]:
        c = Fraction(c)
        if (a, b) not in first:
            first[(a, b)] = c
        da, db = labels[a], labels[b]
        if da is not None:
            if db is None:
                return False, f"edge ({a}->{b}): finite tail beside an infinite head"
            if db > da + c:
                return False, f"edge ({a}->{b}): triangle inequality violated"
    path = cert["path"]
    if not path or path[0] != source or path[-1] != target:
        return False, "path endpoints wrong"
    total = Fraction(0)
    for i in range(1, len(path)):
        key = (path[i - 1], path[i])
        if key not in first:
            return False, f"path hop {key} is not a directed edge"
        total += first[key]
    claimed = parse(cert["claimed_cost"])
    if claimed is None or labels[target] is None:
        return False, "target label or claim missing"
    if total != claimed:
        return False, f"path cost {total} != claimed {claimed}"
    if labels[target] != claimed:
        return False, f"d[target] {labels[target]} != claimed {claimed}"
    return True, "directed certificate verified: path cost equals a proven lower bound"
