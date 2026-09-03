/-
Scaling transfer for the certificate checker (closes the ℚ→ℤ trust gap).

The pipeline's rational certificates are checked after multiplying every edge
cost, label, and the claimed cost by the positive common denominator L of all
values. Rationals with a fixed positive denominator L are exactly the integers
(numerators): comparison and addition of same-denominator fractions ARE integer
comparison and addition of numerators. What still had to be trusted was that
CHECKING THE SCALED INSTANCE says anything about the unscaled one. This file
removes that trust:

  `check_scaled_sound`: for any k > 0, if the verified checker accepts the
  k-scaled certificate, then the ORIGINAL certificate's path is a walk of
  exactly the original claimed cost, and no walk in the ORIGINAL instance from
  source to target is cheaper.

The remaining unverified glue is now only: JSON parsing, and Python's exact
`Fraction` arithmetic producing the numerators (integer arithmetic by
construction).
-/

import Verify.PathCert

namespace Verify.Scaling

open Verify.PathCert

def scaleEdge (k : Int) (e : Edge) : Edge :=
  { e with c := k * e.c }

def scaleCert (k : Int) (cert : Cert) : Cert :=
  { cert with
    claimed := k * cert.claimed
    labels := cert.labels.map (fun l => l.map (k * ·))
    edges := cert.edges.map (scaleEdge k) }

/-- A walk scales: multiply every edge by k and the cost multiplies by k. -/
theorem walk_scale {E : List Edge} {u v : Nat} {cost : Int} (k : Int)
    (h : Walk E u v cost) : Walk (E.map (scaleEdge k)) u v (k * cost) := by
  induction h with
  | nil w =>
      rw [Int.mul_zero]
      exact Walk.nil w
  | cons e u mem step tail ih =>
      rw [Int.mul_add]
      have hc : k * e.c = (scaleEdge k e).c := rfl
      rw [hc]
      exact Walk.cons (scaleEdge k e) u (List.mem_map_of_mem _ mem) step ih

/-- A walk in the scaled instance comes from a walk in the original, with the
cost divided out. -/
theorem walk_unscale {E : List Edge} {k : Int} :
    ∀ {u v : Nat} {cost' : Int}, Walk (E.map (scaleEdge k)) u v cost' →
      ∃ cost, cost' = k * cost ∧ Walk E u v cost := by
  intro u v cost' h
  induction h with
  | nil w => exact ⟨0, by omega, Walk.nil w⟩
  | cons e' u mem step tail ih =>
      obtain ⟨e, heMem, heEq⟩ := List.mem_map.mp mem
      obtain ⟨cost, hc, hw⟩ := ih
      have hcc : e'.c = k * e.c := by rw [← heEq]; rfl
      have ha : e'.a = e.a := by rw [← heEq]; rfl
      have hb : e'.b = e.b := by rw [← heEq]; rfl
      refine ⟨e.c + cost, ?_, ?_⟩
      · rw [hcc, hc, Int.mul_add]
      · rw [ha, hb] at step
        exact Walk.cons e u heMem step hw

/-- k > 0 cancels in ≤, both directions. -/
theorem mul_le_cancel {k a b : Int} (hk : 0 < k) :
    k * a ≤ k * b ↔ a ≤ b := by
  constructor
  · intro h
    exact Int.le_of_mul_le_mul_left h hk
  · intro h
    exact Int.mul_le_mul_of_nonneg_left h (Int.le_of_lt hk)

/-- k > 0 cancels in equality. -/
theorem mul_left_cancel {k a b : Int} (hk : 0 < k) (h : k * a = k * b) :
    a = b := by
  have h1 : k * a ≤ k * b := by rw [h]; exact Int.le_refl _
  have h2 : k * b ≤ k * a := by rw [h]; exact Int.le_refl _
  have hab := (mul_le_cancel hk).mp h1
  have hba := (mul_le_cancel hk).mp h2
  omega

/-- MAIN TRANSFER THEOREM: accepting the k-scaled certificate proves the
original path is a minimum-cost walk in the ORIGINAL instance. -/
theorem check_scaled_sound (k : Int) (hk : 0 < k) (cert : Cert)
    (h : check (scaleCert k cert) = true) :
    Walk cert.edges cert.source cert.target cert.claimed ∧
    (∀ cost, Walk cert.edges cert.source cert.target cost →
      cert.claimed ≤ cost) := by
  obtain ⟨hwalk, hmin⟩ := check_sound (scaleCert k cert) h
  have hE : (scaleCert k cert).edges = cert.edges.map (scaleEdge k) := rfl
  have hclaim : (scaleCert k cert).claimed = k * cert.claimed := rfl
  rw [hE] at hwalk
  constructor
  · obtain ⟨cost, hc, hw⟩ := walk_unscale (E := cert.edges) (k := k) hwalk
    rw [hclaim] at hc
    have hceq : cert.claimed = cost := mul_left_cancel hk hc
    rw [hceq]
    exact hw
  · intro cost hw
    have hscaled := walk_scale (E := cert.edges) k hw
    have hle := hmin (k * cost) (by rw [hE]; exact hscaled)
    rw [hclaim] at hle
    exact (mul_le_cancel hk).mp hle

end Verify.Scaling
