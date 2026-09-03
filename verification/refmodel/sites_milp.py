"""S2 reference model: max-sum site selection as an exact-rational MILP.

Independent of the evaluator: this module derives the candidate set and the
conflict constraints directly from the specification and emits an LP file whose
objective coefficients are the EXACT decimal expansions of the binary32 scores
(a binary32 value always has a finite decimal expansion, so nothing is rounded).
SCIP 10 in exact mode parses LP coefficients as rationals.

    maximize   sum_i s_i x_i
    subject to x_i + x_j <= 1   for every candidate pair with Chebyshev < m
               sum_i x_i <= K
               x binary
"""

from __future__ import annotations

import itertools
from decimal import Decimal

from common.canonical import bits_to_f32, canonical_bytes, sha256_hex


def _local_max_candidates(width: int, height: int, scores: list[float]) -> list[int]:
    # Derived from the spec (positive 3x3 local maxima, plateau tie to the lowest
    # linear index). Deliberately re-stated here rather than imported from the
    # evaluator: the two derivations cross-check each other in run.py.
    result = []
    for y in range(height):
        for x in range(width):
            i = x + y * width
            s = scores[i]
            if s <= 0.0:
                continue
            ok = True
            for dy in (-1, 0, 1):
                ny = y + dy
                if ny < 0 or ny >= height:
                    continue
                for dx in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    nx = x + dx
                    if nx < 0 or nx >= width:
                        continue
                    j = nx + ny * width
                    if scores[j] > s or (scores[j] == s and j < i):
                        ok = False
                        break
                if not ok:
                    break
            if ok:
                result.append(i)
    return result


def build_lp(instance: dict) -> tuple[str, list[int], str]:
    """Returns (lp_text, candidate_cells, model_hash)."""
    data = instance["data"]
    width = data["width"]
    height = data["height"]
    scores = [bits_to_f32(b) for b in data["scores_b32"]]
    separation = data["min_separation"]
    max_sites = data["max_sites"]

    cands = _local_max_candidates(width, height, scores)

    def cheb(a: int, b: int) -> int:
        return max(abs(a % width - b % width), abs(a // width - b // width))

    lines = ["\\ P-SITES exact max-sum site selection", "Maximize", " obj:"]
    terms = []
    for i in cands:
        # Decimal(float) is the exact value of the binary64 (== binary32) score.
        terms.append(f" + {Decimal(scores[i])} x{i}")
    lines.append("".join(terms) if terms else " 0 x_dummy")
    lines.append("Subject To")
    n = 0
    for a, b in itertools.combinations(cands, 2):
        if cheb(a, b) < separation:
            lines.append(f" c{n}: x{a} + x{b} <= 1")
            n += 1
    lines.append(f" budget: {' + '.join(f'x{i}' for i in cands)} <= {max_sites}"
                 if cands else " budget: x_dummy <= 0")
    lines.append("Binary")
    lines.append(" " + " ".join(f"x{i}" for i in cands) if cands else " x_dummy")
    lines.append("End")
    lp = "\n".join(lines) + "\n"
    model_hash = sha256_hex(canonical_bytes({"lp": lp}))
    return lp, cands, model_hash
