/-
Line health (spec §7e, register A8; claims C9.x / CF.8–CF.10).

Three rules of the line verdicts are arithmetic facts the mod relies on, proved
here over exact naturals (the mod computes the same expressions in binary32 /
binary64 with one rounding, which the pipeline's bit-exact re-derivation covers):

1. The fleet rule is MINIMAL. `fleetFor L seats` = max 1 ⌈L / seats⌉ is the least
   fleet of at least one vehicle whose seats (at the target fill, or per day under
   the ceiling — both rules have this shape) carry the load; clamping into the
   game's span keeps a fitting fleet unchanged and a non-fitting one at the
   nearest end of the span.

2. The relative "empty" bar cannot flag more than half the city. The bar is at
   most 35 % of the UPPER MEDIAN occupancy — the value at index n/2 of the sorted
   occupancies — and every line from that index on is at least the median, so at
   most n/2 lines can sit under a bar strictly below it.

3. The verdict order: the game's own fleet flag is judged first whatever else is
   true, and a removal needs both signals (readings empty AND demand under the
   floor at the smallest service).
-/

namespace Verify.LineHealth

/-! ### 1. The fleet rule -/

/-- ⌈a / s⌉ over naturals, as (a + s − 1) / s. -/
def ceilDiv (a s : Nat) : Nat := (a + s - 1) / s

theorem ceilDiv_carries (a s : Nat) (hs : 0 < s) : a ≤ s * ceilDiv a s := by
  unfold ceilDiv
  have h1 := Nat.div_add_mod (a + s - 1) s
  have h2 := Nat.mod_lt (a + s - 1) hs
  omega

theorem ceilDiv_minimal (a s v : Nat) (hs : 0 < s) (h : a ≤ s * v) : ceilDiv a s ≤ v := by
  unfold ceilDiv
  have hlt : a + s - 1 < (v + 1) * s := by
    have : (v + 1) * s = s * v + s := by
      rw [Nat.add_mul, Nat.one_mul, Nat.mul_comm]
    omega
  have := (Nat.div_lt_iff_lt_mul hs).mpr hlt
  omega

/-- The fleet for a load `L` when one vehicle carries `seats` of it: at least one
vehicle, ⌈L / seats⌉ otherwise (`TransitModes.FleetForLoad` / `FleetForDemand`). -/
def fleetFor (L seats : Nat) : Nat := max 1 (ceilDiv L seats)

theorem fleetFor_pos (L seats : Nat) : 1 ≤ fleetFor L seats := by
  unfold fleetFor; omega

/-- The fleet carries the load. -/
theorem fleetFor_carries (L seats : Nat) (hs : 0 < seats) : L ≤ seats * fleetFor L seats := by
  unfold fleetFor
  have h := ceilDiv_carries L seats hs
  have hmono : seats * ceilDiv L seats ≤ seats * max 1 (ceilDiv L seats) :=
    Nat.mul_le_mul_left seats (Nat.le_max_right _ _)
  omega

/-- No smaller fleet of at least one vehicle carries the load: the rule is the
minimum, not a heuristic. -/
theorem fleetFor_minimal (L seats v : Nat) (hs : 0 < seats) (hv : 1 ≤ v) (h : L ≤ seats * v) :
    fleetFor L seats ≤ v := by
  unfold fleetFor
  have := ceilDiv_minimal L seats v hs h
  omega

/-- The game's span: `clamp v lo hi` = min hi (max lo v) (`TransitModes.Clamp`). -/
def clamp (v lo hi : Nat) : Nat := min hi (max lo v)

theorem clamp_in_span (v lo hi : Nat) (h : lo ≤ hi) : lo ≤ clamp v lo hi ∧ clamp v lo hi ≤ hi := by
  unfold clamp; omega

/-- A fleet the span admits is left as it is. -/
theorem clamp_of_fits (v lo hi : Nat) (hlo : lo ≤ v) (hhi : v ≤ hi) : clamp v lo hi = v := by
  unfold clamp; omega

/-- A fleet the span does not admit lands on the end nearest to it — so a load past
the span's top reads as "the largest allowed fleet, overloaded", which is what the
ladder climbs on. -/
theorem clamp_of_over (v lo hi : Nat) (h : hi < v) : clamp v lo hi = hi := by
  unfold clamp; omega

theorem clamp_of_under (v lo hi : Nat) (h : v < lo) (hlh : lo ≤ hi) : clamp v lo hi = lo := by
  unfold clamp; omega

/-! ### 2. The upper-median bar -/

/-- The upper median: the element at index n/2 of the sorted list (0 for an empty
list) — `LineHealthRules.UpperMedian` on an already sorted list. -/
def upperMedian (l : List Nat) : Nat := (l.drop (l.length / 2)).headD 0

theorem filter_nil_of_none {α : Type} (p : α → Bool) :
    ∀ l : List α, (∀ a ∈ l, p a = false) → l.filter p = []
  | [], _ => rfl
  | a :: t, h => by
      have ha : p a = false := h a (List.mem_cons_self a t)
      have ht : ∀ b ∈ t, p b = false := fun b hb => h b (List.mem_cons_of_mem a hb)
      simp [List.filter, ha, filter_nil_of_none p t ht]

/-- In a sorted list, everything from index k on is at least the element at k. -/
theorem drop_ge_head (l : List Nat) (hs : l.Pairwise (· ≤ ·)) (k : Nat) :
    ∀ y ∈ l.drop k, (l.drop k).headD 0 ≤ y := by
  have hsub : (l.drop k).Pairwise (· ≤ ·) := hs.sublist (List.drop_sublist k l)
  intro y hy
  cases hd : l.drop k with
  | nil => rw [hd] at hy; exact absurd hy (List.not_mem_nil y)
  | cons h t =>
      rw [hd] at hy hsub
      rw [List.pairwise_cons] at hsub
      simp only [List.headD_cons]
      rcases List.mem_cons.mp hy with hy | hy
      · rw [hy]; exact Nat.le_refl _
      · exact hsub.1 y hy

/-- MAIN: with a bar strictly under the upper median, at most n/2 of n sorted
occupancies fall under the bar. -/
theorem at_most_half_under (l : List Nat) (hs : l.Pairwise (· ≤ ·)) (t : Nat)
    (ht : t < upperMedian l) :
    (l.filter (fun x => decide (x ≤ t))).length ≤ l.length / 2 := by
  have hsplit := List.take_append_drop (l.length / 2) l
  have hnone : ∀ y ∈ l.drop (l.length / 2), (fun x => decide (x ≤ t)) y = false := by
    intro y hy
    have := drop_ge_head l hs (l.length / 2) y hy
    unfold upperMedian at ht
    simp only [decide_eq_false_iff_not, Nat.not_le]
    omega
  have hdrop := filter_nil_of_none (fun x => decide (x ≤ t)) _ hnone
  calc (l.filter (fun x => decide (x ≤ t))).length
      = ((l.take (l.length / 2) ++ l.drop (l.length / 2)).filter (fun x => decide (x ≤ t))).length := by
        rw [hsplit]
    _ = ((l.take (l.length / 2)).filter (fun x => decide (x ≤ t))).length
          + ((l.drop (l.length / 2)).filter (fun x => decide (x ≤ t))).length := by
        rw [List.filter_append, List.length_append]
    _ = ((l.take (l.length / 2)).filter (fun x => decide (x ≤ t))).length := by
        rw [hdrop]; simp
    _ ≤ (l.take (l.length / 2)).length := List.length_filter_le _ _
    _ ≤ l.length / 2 := by rw [List.length_take]; exact Nat.min_le_left _ _

/-- The bar of the mod, in scaled naturals: 100·t ≤ 35·median (EmptyShareOfMedian)
puts it strictly under a positive median, so the bound above applies. -/
theorem bar_below_median (t m : Nat) (hm : 0 < m) (hbar : 100 * t ≤ 35 * m) : t < m := by
  omega

theorem at_most_half_empty (l : List Nat) (hs : l.Pairwise (· ≤ ·)) (t : Nat)
    (hm : 0 < upperMedian l) (hbar : 100 * t ≤ 35 * upperMedian l) :
    (l.filter (fun x => decide (x ≤ t))).length ≤ l.length / 2 :=
  at_most_half_under l hs t (bar_below_median t _ hm hbar)

/-! ### 3. The verdict order -/

inductive Verdict where
  | healthy | fleetShort | modeUp | splitRoute | remove | modeDown | fleetUp | fleetDown | schedule
deriving Repr, DecidableEq

/-- The inputs the tree reads (`LineHealthRules.VerdictOf`). -/
structure Signals where
  gameShort : Bool          -- NotEnoughVehicles ∨ (RequireVehicles ∧ vehicles < target)
  chosenAbove : Bool        -- the ladder settled above the line's mode
  split : Bool              -- no rung carries the load
  measuredEmpty : Bool      -- mean and peak under the relative bar
  demandUnderFloor : Bool   -- utilisation at the smallest service's fewest vehicles < floor
  chosenBelow : Bool
  fleetAbove : Bool         -- recommended fleet > running
  fleetBelow : Bool
  scheduleDiffers : Bool

def verdict (s : Signals) : Verdict :=
  if s.gameShort then .fleetShort
  else if s.chosenAbove then .modeUp
  else if s.split then .splitRoute
  else if s.measuredEmpty && s.demandUnderFloor then .remove
  else if s.chosenBelow then .modeDown
  else if s.fleetAbove then .fleetUp
  else if s.fleetBelow then .fleetDown
  else if s.scheduleDiffers then .schedule
  else .healthy

/-- The game's own flag is judged first, whatever the readings and the demand say. -/
theorem fleetShort_first (s : Signals) (h : s.gameShort = true) : verdict s = .fleetShort := by
  simp [verdict, h]

/-- A removal needs BOTH signals: readings empty and demand under the floor. -/
theorem remove_needs_both (s : Signals) (h : verdict s = .remove) :
    s.measuredEmpty = true ∧ s.demandUnderFloor = true := by
  unfold verdict at h
  repeat' split at h
  all_goals simp_all

/-- Readings alone never remove a line. -/
theorem no_remove_without_demand (s : Signals) (hd : s.demandUnderFloor = false) :
    verdict s ≠ .remove := by
  intro h
  have := remove_needs_both s h
  rw [hd] at this
  exact Bool.false_ne_true this.2

end Verify.LineHealth
