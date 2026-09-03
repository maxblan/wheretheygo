/-
Shortest-path certificate soundness, and a VERIFIED executable checker.

The certificate (emitted by the pipeline as `path-certificate-int.json`, all
values integer-scaled by a common positive denominator) consists of distance
labels `d : Nat → Option Int` (`none` = unreachable), a claimed cost, and a
node path. The soundness theorem `check_sound` proves: if the Boolean checker
accepts, then the claimed path IS a walk of the claimed cost, and EVERY walk
from source to target costs at least that — i.e. the path is minimum-cost.

Trust story: only `check` (a small executable function) and the instance data
need to be trusted at runtime; its meaning is pinned by machine-checked proof.
The rational→integer scaling happens in Python and is documented as a trusted
10-line transformation (docs/verification-architecture.md).
-/

namespace Verify.PathCert

structure Edge where
  a : Nat
  b : Nat
  c : Int
deriving Repr, DecidableEq

/-- Distance labels; `none` means "no finite label" (unreachable). -/
abbrev Labels := Nat → Option Int

/-- Certificate conditions for one undirected edge: a finite label forces a
finite label across the edge, within cost `c` in both directions. -/
def EdgeOk (d : Labels) (e : Edge) : Prop :=
  (∀ da, d e.a = some da → ∃ db, d e.b = some db ∧ db ≤ da + e.c) ∧
  (∀ db, d e.b = some db → ∃ da, d e.a = some da ∧ da ≤ db + e.c)

/-- Undirected walks over an edge list, accumulating exact integer cost. -/
inductive Walk (E : List Edge) : Nat → Nat → Int → Prop
  | nil (v : Nat) : Walk E v v 0
  | cons {v w : Nat} {rest : Int} (e : Edge) (u : Nat)
      (mem : e ∈ E)
      (step : (e.a = u ∧ e.b = v) ∨ (e.a = v ∧ e.b = u))
      (tail : Walk E v w rest) : Walk E u w (e.c + rest)

/-- Core lemma: along any walk, labels can increase by at most the walk cost. -/
theorem walk_lower_bound {E : List Edge} {d : Labels}
    (hok : ∀ e ∈ E, EdgeOk d e) :
    ∀ {u w : Nat} {cost : Int}, Walk E u w cost →
      ∀ du, d u = some du → ∃ dw, d w = some dw ∧ dw ≤ du + cost := by
  intro u w cost hwalk
  induction hwalk with
  | nil v =>
      intro du hdu
      exact ⟨du, hdu, by omega⟩
  | cons e u mem step tail ih =>
      intro du hdu
      have he := hok e mem
      rcases step with ⟨ha, hb⟩ | ⟨ha, hb⟩
      · -- traverse a -> b
        obtain ⟨dv, hdv, hle⟩ := he.1 du (by rw [ha]; exact hdu)
        obtain ⟨dw, hdw, hle'⟩ := ih dv (by rw [← hb]; exact hdv)
        exact ⟨dw, hdw, by omega⟩
      · -- traverse b -> a
        obtain ⟨dv, hdv, hle⟩ := he.2 du (by rw [hb]; exact hdu)
        obtain ⟨dw, hdw, hle'⟩ := ih dv (by rw [← ha]; exact hdv)
        exact ⟨dw, hdw, by omega⟩

/-- Soundness of the label certificate: with `d src = 0` and all edges ok,
`d t` lower-bounds every walk from `src` to `t`. -/
theorem certificate_sound {E : List Edge} {d : Labels} {src t : Nat} {dt : Int}
    (hsrc : d src = some 0)
    (hok : ∀ e ∈ E, EdgeOk d e)
    (ht : d t = some dt) :
    ∀ cost, Walk E src t cost → dt ≤ cost := by
  intro cost hwalk
  obtain ⟨dw, hdw, hle⟩ := walk_lower_bound hok hwalk 0 hsrc
  have : dw = dt := by rw [hdw] at ht; exact Option.some.inj ht
  omega

/- ------------------------------------------------------------------ -/
/- Executable checker and its reflection to the propositions above.    -/
/- ------------------------------------------------------------------ -/

/-- Label lookup from a list; out of range = `none`. -/
def labelAt (labels : List (Option Int)) (n : Nat) : Option Int :=
  (labels.get? n).bind id

/-- Boolean edge condition. -/
def edgeOkB (labels : List (Option Int)) (e : Edge) : Bool :=
  match labelAt labels e.a, labelAt labels e.b with
  | some da, some db => decide (db ≤ da + e.c) && decide (da ≤ db + e.c)
  | none, none => true
  | _, _ => false

theorem edgeOkB_sound {labels : List (Option Int)} {e : Edge}
    (h : edgeOkB labels e = true) : EdgeOk (labelAt labels) e := by
  unfold edgeOkB at h
  constructor
  · intro da hda
    cases hb : labelAt labels e.b with
    | some db =>
        refine ⟨db, rfl, ?_⟩
        rw [hda, hb] at h
        simp only [Bool.and_eq_true, decide_eq_true_eq] at h
        exact h.1
    | none => rw [hda, hb] at h; simp at h
  · intro db hdb
    cases ha : labelAt labels e.a with
    | some da =>
        refine ⟨da, rfl, ?_⟩
        rw [ha, hdb] at h
        simp only [Bool.and_eq_true, decide_eq_true_eq] at h
        exact h.2
    | none => rw [ha, hdb] at h; simp at h

/-- First declared edge joining `u` and `v` (either orientation). -/
def findEdge (E : List Edge) (u v : Nat) : Option Edge :=
  E.find? fun e => (e.a == u && e.b == v) || (e.a == v && e.b == u)

theorem findEdge_sound {E : List Edge} {u v : Nat} {e : Edge}
    (h : findEdge E u v = some e) :
    e ∈ E ∧ ((e.a = u ∧ e.b = v) ∨ (e.a = v ∧ e.b = u)) := by
  unfold findEdge at h
  have mem := List.mem_of_find?_eq_some h
  have prop := List.find?_some h
  simp only [Bool.or_eq_true, Bool.and_eq_true, beq_iff_eq] at prop
  exact ⟨mem, prop⟩

/-- Cost of a node path using the first matching edge per hop. -/
def pathCost (E : List Edge) : List Nat → Option Int
  | [] => none
  | [_] => some 0
  | u :: v :: rest =>
      match findEdge E u v, pathCost E (v :: rest) with
      | some e, some r => some (e.c + r)
      | _, _ => none

/-- A priced path is a real walk to its last node. -/
theorem pathCost_walk {E : List Edge} :
    ∀ (l : List Nat) (u : Nat) (cost : Int),
      pathCost E (u :: l) = some cost →
      ∃ w, (u :: l).getLast? = some w ∧ Walk E u w cost := by
  intro l
  induction l with
  | nil =>
      intro u cost h
      simp only [pathCost] at h
      exact ⟨u, rfl, by rw [← Option.some.inj h]; exact Walk.nil u⟩
  | cons v rest ih =>
      intro u cost h
      simp only [pathCost] at h
      cases hf : findEdge E u v with
      | none => rw [hf] at h; simp at h
      | some e =>
          rw [hf] at h
          cases hr : pathCost E (v :: rest) with
          | none => rw [hr] at h; simp at h
          | some r =>
              rw [hr] at h
              obtain ⟨w, hlast, hwalk⟩ := ih v r hr
              obtain ⟨mem, step⟩ := findEdge_sound hf
              refine ⟨w, ?_, ?_⟩
              · rw [List.getLast?_cons_cons]; exact hlast
              · rw [← Option.some.inj h]
                exact Walk.cons e u mem step hwalk

/-- The self-contained integer certificate. -/
structure Cert where
  source : Nat
  target : Nat
  claimed : Int
  labels : List (Option Int)
  path : List Nat
  edges : List Edge

/-- The verified checker. -/
def check (cert : Cert) : Bool :=
  (labelAt cert.labels cert.source == some 0)
  && cert.edges.all (edgeOkB cert.labels)
  && (match cert.path with
      | u :: _ => u == cert.source
      | [] => false)
  && (cert.path.getLast? == some cert.target)
  && (pathCost cert.edges cert.path == some cert.claimed)
  && (labelAt cert.labels cert.target == some cert.claimed)

/-- MAIN THEOREM. If `check` accepts, the certified path is a walk of exactly
the claimed cost, and no walk from source to target is cheaper. -/
theorem check_sound (cert : Cert) (h : check cert = true) :
    Walk cert.edges cert.source cert.target cert.claimed ∧
    (∀ cost, Walk cert.edges cert.source cert.target cost → cert.claimed ≤ cost) := by
  unfold check at h
  simp only [Bool.and_eq_true, beq_iff_eq] at h
  obtain ⟨⟨⟨⟨⟨hsrc, hedges⟩, hhead⟩, hlast⟩, hcost⟩, htarget⟩ := h
  have hok : ∀ e ∈ cert.edges, EdgeOk (labelAt cert.labels) e := by
    intro e he
    exact edgeOkB_sound (List.all_eq_true.mp hedges e he)
  cases hp : cert.path with
  | nil => rw [hp] at hhead; simp at hhead
  | cons u l =>
      rw [hp] at hhead
      simp only [beq_iff_eq] at hhead
      subst hhead
      rw [hp] at hcost hlast
      obtain ⟨w, hw, hwalk⟩ := pathCost_walk (E := cert.edges) l cert.source
        cert.claimed hcost
      have hwt : w = cert.target := by
        rw [hw] at hlast; exact Option.some.inj hlast
      subst hwt
      refine ⟨hwalk, ?_⟩
      intro cost hcost'
      exact certificate_sound hsrc hok htarget cost hcost'

end Verify.PathCert
