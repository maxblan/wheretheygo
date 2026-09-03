/-
Boardings arithmetic (spec §6.1/§6.2, claim C7.2).

The transit graph prices a line use with ONE undirected access edge carrying
half the boarding cost, because a rider traverses it twice — on and off. The
mod counts boardings as (access edges on the itinerary) / 2 and transfers as
boardings − 1. This file proves the arithmetic identity that makes that
correct, and the cost identity behind the fixed free-transfer bug: every leg
pays the full boarding cost exactly once.

Model: an itinerary is a list of segments; a `leg` (one vehicle used, with any
number of ride edges aboard) encodes as access :: rides ++ [access]; a walk
segment encodes as a single walk edge. This matches the graph construction:
Access edges are exactly the stop↔line-stop links, entered once and left once
per vehicle used.
-/

namespace Verify.Boardings

inductive EdgeKind where
  | walk
  | access
  | ride
deriving Repr, DecidableEq

/-- One segment of an itinerary. -/
inductive Segment where
  | walk
  | leg (rides : Nat)
deriving Repr

def encodeSegment : Segment → List EdgeKind
  | .walk => [.walk]
  | .leg rides => .access :: (List.replicate rides .ride ++ [.access])

def encode (it : List Segment) : List EdgeKind :=
  it.flatMap encodeSegment

def countAccess : List EdgeKind → Nat
  | [] => 0
  | .access :: t => countAccess t + 1
  | _ :: t => countAccess t

def legCount : List Segment → Nat
  | [] => 0
  | .leg _ :: t => legCount t + 1
  | .walk :: t => legCount t

theorem countAccess_append (l₁ l₂ : List EdgeKind) :
    countAccess (l₁ ++ l₂) = countAccess l₁ + countAccess l₂ := by
  induction l₁ with
  | nil => simp [countAccess]
  | cons h t ih =>
      cases h <;> simp [countAccess, ih] <;> omega

theorem countAccess_replicate_ride (n : Nat) :
    countAccess (List.replicate n .ride) = 0 := by
  induction n with
  | zero => rfl
  | succ n ih => simp [List.replicate, countAccess, ih]

theorem countAccess_segment (s : Segment) :
    countAccess (encodeSegment s) =
      match s with
      | .walk => 0
      | .leg _ => 2 := by
  cases s with
  | walk => rfl
  | leg rides =>
      simp [encodeSegment, countAccess, countAccess_append,
            countAccess_replicate_ride]

/-- Every vehicle used contributes exactly two access edges. -/
theorem countAccess_encode (it : List Segment) :
    countAccess (encode it) = 2 * legCount it := by
  induction it with
  | nil => rfl
  | cons s t ih =>
      cases s with
      | walk =>
          simp [encode, List.flatMap_cons, countAccess_append,
                countAccess_segment, legCount] at *
          omega
      | leg rides =>
          simp [encode, List.flatMap_cons, countAccess_append,
                countAccess_segment, legCount] at *
          omega

/-- The mod's boarding count (access edges / 2) equals the vehicles used. -/
theorem boardings_eq_legs (it : List Segment) :
    countAccess (encode it) / 2 = legCount it := by
  rw [countAccess_encode]
  omega

/-- Transfers = boardings − 1 = vehicles used − 1. -/
theorem transfers_eq (it : List Segment) :
    countAccess (encode it) / 2 - 1 = legCount it - 1 := by
  rw [boardings_eq_legs]

/- ------------------------------------------------------------------ -/
/- Cost identity: half-cost access edges price each leg exactly once.  -/
/- ------------------------------------------------------------------ -/

/-- Total access cost of an itinerary at `half` per access edge. -/
def accessCost (half : Int) : List EdgeKind → Int
  | [] => 0
  | .access :: t => accessCost half t + half
  | _ :: t => accessCost half t

/-- Full boarding cost summed once per vehicle used. -/
def totalBoardCost (full : Int) : List Segment → Int
  | [] => 0
  | .leg _ :: t => totalBoardCost full t + full
  | .walk :: t => totalBoardCost full t

theorem accessCost_append (half : Int) (l₁ l₂ : List EdgeKind) :
    accessCost half (l₁ ++ l₂) = accessCost half l₁ + accessCost half l₂ := by
  induction l₁ with
  | nil => simp [accessCost]
  | cons h t ih => cases h <;> simp [accessCost, ih] <;> omega

theorem accessCost_replicate_ride (half : Int) (n : Nat) :
    accessCost half (List.replicate n .ride) = 0 := by
  induction n with
  | zero => rfl
  | succ n ih => simp [List.replicate, accessCost, ih]

theorem accessCost_segment (half : Int) (s : Segment) :
    accessCost half (encodeSegment s) =
      match s with
      | .walk => 0
      | .leg _ => half + half := by
  cases s with
  | walk => rfl
  | leg rides =>
      simp [encodeSegment, accessCost, accessCost_append,
            accessCost_replicate_ride]

/-- With the access edge at HALF the boarding cost, an itinerary pays the FULL
boarding cost (`half + half`) exactly once per vehicle used — the invariant
whose violation was the free-transfer bug (a zero-cost alight edge made
boardings free backwards). -/
theorem accessCost_full_per_leg (it : List Segment) (half : Int) :
    accessCost half (encode it) = totalBoardCost (half + half) it := by
  induction it with
  | nil => rfl
  | cons s t ih =>
      cases s with
      | walk =>
          simp [encode, List.flatMap_cons, accessCost_append,
                accessCost_segment, totalBoardCost] at *
          omega
      | leg rides =>
          simp [encode, List.flatMap_cons, accessCost_append,
                accessCost_segment, totalBoardCost] at *
          omega

end Verify.Boardings
