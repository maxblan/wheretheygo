/-
Directed variant of Verify.PathCert: shortest-path certificate soundness and a
VERIFIED executable checker for DIRECTED graphs — the road network as a vehicle
drives it (one-way streets, turn-dependent costs folded into an explicit state
graph by the pipeline). An `Edge` here is traversable from `a` to `b` only.
`check_sound` proves: if the Boolean checker accepts, the claimed path is a
directed walk of the claimed cost and no directed walk from source to target is
cheaper. Everything is a one-directional restatement of PathCert; the proofs
follow the same shape with the reverse-orientation case removed.
-/

namespace Verify.DirPathCert

structure Edge where
  a : Nat
  b : Nat
  c : Int
deriving Repr, DecidableEq

abbrev Labels := Nat → Option Int

/-- Certificate condition for one directed edge: a finite label at the tail
forces a finite label at the head, within cost `c`. -/
def EdgeOk (d : Labels) (e : Edge) : Prop :=
  ∀ da, d e.a = some da → ∃ db, d e.b = some db ∧ db ≤ da + e.c

/-- Directed walks over an edge list, accumulating exact integer cost. -/
inductive Walk (E : List Edge) : Nat → Nat → Int → Prop
  | nil (v : Nat) : Walk E v v 0
  | cons {v w : Nat} {rest : Int} (e : Edge) (u : Nat)
      (mem : e ∈ E) (step : e.a = u ∧ e.b = v)
      (tail : Walk E v w rest) : Walk E u w (e.c + rest)

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
      obtain ⟨ha, hb⟩ := step
      obtain ⟨dv, hdv, hle⟩ := he du (by rw [ha]; exact hdu)
      obtain ⟨dw, hdw, hle'⟩ := ih dv (by rw [← hb]; exact hdv)
      exact ⟨dw, hdw, by omega⟩

theorem certificate_sound {E : List Edge} {d : Labels} {src t : Nat} {dt : Int}
    (hsrc : d src = some 0)
    (hok : ∀ e ∈ E, EdgeOk d e)
    (ht : d t = some dt) :
    ∀ cost, Walk E src t cost → dt ≤ cost := by
  intro cost hwalk
  obtain ⟨dw, hdw, hle⟩ := walk_lower_bound hok hwalk 0 hsrc
  have : dw = dt := by rw [hdw] at ht; exact Option.some.inj ht
  omega

def labelAt (labels : List (Option Int)) (n : Nat) : Option Int :=
  (labels.get? n).bind id

/-- Boolean edge condition, tail to head only. -/
def edgeOkB (labels : List (Option Int)) (e : Edge) : Bool :=
  match labelAt labels e.a, labelAt labels e.b with
  | some da, some db => decide (db ≤ da + e.c)
  | none, _ => true
  | some _, none => false

theorem edgeOkB_sound {labels : List (Option Int)} {e : Edge}
    (h : edgeOkB labels e = true) : EdgeOk (labelAt labels) e := by
  unfold edgeOkB at h
  intro da hda
  cases hb : labelAt labels e.b with
  | some db =>
      refine ⟨db, rfl, ?_⟩
      rw [hda, hb] at h
      simp only [decide_eq_true_eq] at h
      exact h
  | none => rw [hda, hb] at h; simp at h

/-- First declared edge from `u` to `v`. -/
def findEdge (E : List Edge) (u v : Nat) : Option Edge :=
  E.find? fun e => e.a == u && e.b == v

theorem findEdge_sound {E : List Edge} {u v : Nat} {e : Edge}
    (h : findEdge E u v = some e) :
    e ∈ E ∧ (e.a = u ∧ e.b = v) := by
  unfold findEdge at h
  have mem := List.mem_of_find?_eq_some h
  have prop := List.find?_some h
  simp only [Bool.and_eq_true, beq_iff_eq] at prop
  exact ⟨mem, prop⟩

def pathCost (E : List Edge) : List Nat → Option Int
  | [] => none
  | [_] => some 0
  | u :: v :: rest =>
      match findEdge E u v, pathCost E (v :: rest) with
      | some e, some r => some (e.c + r)
      | _, _ => none

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

structure Cert where
  source : Nat
  target : Nat
  claimed : Int
  labels : List (Option Int)
  path : List Nat
  edges : List Edge

def check (cert : Cert) : Bool :=
  (labelAt cert.labels cert.source == some 0)
  && cert.edges.all (edgeOkB cert.labels)
  && (match cert.path with
      | u :: _ => u == cert.source
      | [] => false)
  && (cert.path.getLast? == some cert.target)
  && (pathCost cert.edges cert.path == some cert.claimed)
  && (labelAt cert.labels cert.target == some cert.claimed)

/-- MAIN THEOREM (directed). -/
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

end Verify.DirPathCert
