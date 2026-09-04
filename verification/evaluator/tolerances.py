"""Float error budgets for comparing the subject's binary32 results against the
evaluator's exact rationals — derived, not guessed.

Wherever both implementations run the SAME operation sequence in binary32 (S5,
S6, edge costs, zone mapping, the heatmap terms) the comparison is EXACT and no
budget exists. A budget is used only where the subject ACCUMULATES in binary32
what the evaluator computes in exact arithmetic:

  * CreditLine's credited sum:  credited += w_i * (float)Math.Pow(d, t_i)
  * Dijkstra's path distance:   dist += cost_e  along the returned path

Model (Higham, "Accuracy and Stability of Numerical Algorithms", 2nd ed., §3.1):
every binary32 operation returns fl(x op y) = (x op y)(1 + δ), |δ| <= u with
u = 2^-24 the unit roundoff of round-to-nearest. A quantity carrying k such
factors satisfies |Π(1 + δ_i) - 1| <= γ_k := k u / (1 - k u)   (Lemma 3.1, eq. 3.4).
Recursive summation of m terms x_i costs at most m - 1 roundings on any one term,
so |fl(Σ x_i) - Σ x_i| <= γ_{m-1} Σ|x_i|   (eq. 4.4).

Credit: each term is w_i * pow(...) — the pow (double, then cast) counted as one
rounding, the multiply one, plus at most m - 1 from the summation: k = m + 1 per
term, and Σ|x_i| <= the exact interval's upper end `hi` (every term is a
non-negative credit). Hence the budget below. Distances: L edges, L - 1 adds,
Σ|x_i| = the exact path cost.

The budgets are rigorous bounds on ROUNDING alone. They do not cover a float32
Dijkstra retaining a near-shortest itinerary that exact arithmetic rejects — that
is a decision flip, and the evaluator reports it as such rather than absorbing
it into a tolerance.

Run this file directly for an empirical sanity check of the bound against
random float32 summations.
"""

from __future__ import annotations

from fractions import Fraction

UNIT_ROUNDOFF = Fraction(1, 2 ** 24)


def gamma(k: int) -> Fraction:
    """Higham's γ_k = k u / (1 - k u); requires k u < 1 (k < 2^24)."""
    ku = k * UNIT_ROUNDOFF
    if ku >= 1:
        raise ValueError("too many roundings for the bound to hold")
    return ku / (1 - ku)


def credit_budget(magnitude: Fraction, pair_count: int) -> Fraction:
    """|fl(credited) - credited| <= γ_{m+1} * Σ|terms|, with Σ|terms| <= magnitude."""
    return gamma(pair_count + 1) * magnitude


def distance_budget(magnitude: Fraction, edge_count: int) -> Fraction:
    """|fl(dist) - dist| <= γ_{L-1} * cost for an L-edge path (L - 1 additions)."""
    return gamma(max(edge_count - 1, 0)) * magnitude


def _selftest(trials: int = 20000, seed: int = 20260904) -> None:
    """Empirical check: random float32 sums must sit inside the derived budget.
    A bound that random inputs can breach is wrong; one they never approach may
    be loose — both are reported."""
    import random
    import struct

    def r32(x: float) -> float:
        return struct.unpack("<f", struct.pack("<f", x))[0]

    rng = random.Random(seed)
    worst_ratio = Fraction(0)
    for _ in range(trials):
        m = rng.randint(1, 64)
        terms = [r32(rng.uniform(0, 1000)) for _ in range(m)]
        acc = 0.0
        for t in terms:
            acc = r32(acc + t)
        exact = sum(Fraction(t) for t in terms)
        err = abs(Fraction(acc) - exact)
        budget = gamma(m - 1) * exact if m > 1 else Fraction(0)
        if err > budget:
            raise AssertionError(f"bound breached: m={m} err={float(err)} budget={float(budget)}")
        if budget:
            worst_ratio = max(worst_ratio, err / budget)
    print(f"selftest ok: {trials} random float32 sums within γ_(m-1)·Σ|x|; "
          f"worst observed error/budget = {float(worst_ratio):.3f}")


if __name__ == "__main__":
    _selftest()
