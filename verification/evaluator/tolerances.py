"""Float error budgets for comparing the subject's binary32 results against the
evaluator's exact rationals.

Everywhere the two implementations compute the SAME operation sequence in
binary32 (S5, S6, edge costs, zone mapping) the comparison is EXACT — no budget.
A budget exists only where the subject accumulates in binary32 what the evaluator
computes in exact arithmetic:

- credited sums (CreditLine): per pair ~a dozen f32 ops (adds, one pow, one mul),
  then one f32 add per pair into the total. Relative error per op <= 2^-24.
- path distances: one f32 add per edge along the path.

The budgets below are deliberately loose by a factor >= 16; any DECISION that
flips inside a budget is reported as DECISION-SENSITIVE rather than passed.
"""

from __future__ import annotations

from fractions import Fraction

ULP32 = Fraction(1, 2 ** 24)


def credit_budget(magnitude: Fraction, pair_count: int) -> Fraction:
    """Absolute budget for a credited sum of `pair_count` pairs totalling about
    `magnitude`: 16 * (pairs + 16) ops * ulp * (magnitude + 1)."""
    ops = Fraction(16 * (pair_count + 16))
    return ops * ULP32 * (magnitude + 1)


def distance_budget(magnitude: Fraction, edge_count: int) -> Fraction:
    ops = Fraction(16 * (edge_count + 4))
    return ops * ULP32 * (magnitude + 1)
