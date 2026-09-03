/-
Calling-point invariants (spec §4/S5), formalized over exact arithmetic.

The mod's PlanCallingPoints computes offsets L·i/n. Over the rationals that is
the numerator sequence (L·i) over the fixed positive denominator n, so the
invariants are integer statements:

  * strict monotonicity of the offsets,
  * first offset 0, last offset exactly L,
  * even intervals (consecutive numerator difference is constant = L).

SelectCallingPoints' keep-rule invariants:

  * both termini are always kept,
  * a must-call window is always kept,
  * the degenerate case that shipped as a bug elsewhere: with no positive
    score the floor is 0, and an all-zero line keeps every window.

The float32-vs-exact correspondence of the C# implementation is established
separately by the pipeline's bit-exact differential test (calling-points
instance); these theorems pin the mathematical content of the rule itself.
-/

namespace Verify.CallingPoints

/-- Numerator of the i-th planned offset (value = L·i / n over denominator n). -/
def offsetNum (L : Int) (i : Nat) : Int := L * i

theorem offset_first (L : Int) : offsetNum L 0 = 0 := by
  simp [offsetNum]

/-- The last offset is exactly the line length: (L·n)/n = L for n ≠ 0. -/
theorem offset_last (L : Int) (n : Nat) (hn : (n : Int) ≠ 0) :
    offsetNum L n / n = L :=
  Int.mul_ediv_cancel L hn

/-- Even intervals: every consecutive numerator difference is exactly L. -/
theorem offset_even (L : Int) (i : Nat) :
    offsetNum L (i + 1) - offsetNum L i = L := by
  unfold offsetNum
  have hcast : ((i + 1 : Nat) : Int) = (i : Int) + 1 := by omega
  rw [hcast, Int.mul_add, Int.mul_one]
  omega

/-- Strict monotonicity of the offsets for a positive length. -/
theorem offset_strict_mono (L : Int) (hL : 0 < L) {i j : Nat} (hij : i < j) :
    offsetNum L i < offsetNum L j := by
  have hij' : (i : Int) < (j : Int) := by exact_mod_cast hij
  calc L * i < L * j := Int.mul_lt_mul_of_pos_left hij' hL
    _ = offsetNum L j := rfl

/-- Offsets never leave the line: L·i ≤ L·n for i ≤ n (0 ≤ L). -/
theorem offset_bounded (L : Int) (hL : 0 ≤ L) {i n : Nat} (hin : i ≤ n) :
    offsetNum L i ≤ offsetNum L n := by
  have hin' : (i : Int) ≤ (n : Int) := by exact_mod_cast hin
  exact Int.mul_le_mul_of_nonneg_left hin' hL

/- ------------------------------------------------------------------ -/
/- SelectCallingPoints keep rule.                                      -/
/- ------------------------------------------------------------------ -/

/-- The positive median: k-th smallest (k = ⌊count/2⌋) of the strictly
positive scores, 0 when none is positive — the exact selection semantics of
PositivePercentile at percentile 0.5. -/
def positiveMedian (scores : List Int) : Int :=
  let pos := (scores.filter (fun s => decide (0 < s))).mergeSort
    (fun a b => decide (a ≤ b))
  pos.getD (pos.length / 2) 0

/-- The keep rule for window `i` of `count` windows. -/
def keep (scores : List Int) (floor : Int) (mustCall : List Bool) (i : Nat) : Bool :=
  i == 0 || i == scores.length - 1 || mustCall.getD i false
    || decide (floor ≤ scores.getD i 0)

theorem keep_first (scores : List Int) (floor : Int) (mustCall : List Bool) :
    keep scores floor mustCall 0 = true := by
  simp [keep]

theorem keep_last (scores : List Int) (floor : Int) (mustCall : List Bool) :
    keep scores floor mustCall (scores.length - 1) = true := by
  simp [keep]

theorem keep_mustCall (scores : List Int) (floor : Int) (mustCall : List Bool)
    (i : Nat) (h : mustCall.getD i false = true) :
    keep scores floor mustCall i = true := by
  unfold keep
  simp only [Bool.or_eq_true, beq_iff_eq, decide_eq_true_eq]
  exact Or.inl (Or.inr h)

/-- No positive score ⇒ the positive median is 0. -/
theorem positiveMedian_of_nonpos (scores : List Int)
    (h : ∀ s ∈ scores, s ≤ 0) : positiveMedian scores = 0 := by
  have hempty : scores.filter (fun s => 0 < s) = [] := by
    apply List.filter_eq_nil_iff.mpr
    intro s hs
    have := h s hs
    simp
    omega
  simp [positiveMedian, hempty, List.mergeSort]

/-- The degenerate case: an all-zero line keeps EVERY window (floor collapses
to 0 and every score meets it). `floor = positiveMedian · share` with any
share: 0 · share = 0. -/
theorem keep_all_when_all_zero (scores : List Int) (share : Int)
    (mustCall : List Bool) (h : ∀ s ∈ scores, s = 0) :
    ∀ i, i < scores.length →
      keep scores (positiveMedian scores * share) mustCall i = true := by
  intro i hi
  have hmed : positiveMedian scores = 0 :=
    positiveMedian_of_nonpos scores (fun s hs => by have := h s hs; omega)
  have hscore : scores[i]?.getD 0 = 0 := by
    cases hg : scores[i]? with
    | none => simp
    | some s =>
        have hmem : s ∈ scores := by
          have hlt := List.getElem?_eq_some_iff.mp hg
          obtain ⟨hidx, hval⟩ := hlt
          exact hval ▸ List.getElem_mem hidx
        have := h s hmem
        simp [this]
  unfold keep
  simp only [Bool.or_eq_true, beq_iff_eq, decide_eq_true_eq]
  refine Or.inr ?_
  rw [hmed, Int.zero_mul, List.getD_eq_getElem?_getD, hscore]
  omega

end Verify.CallingPoints
