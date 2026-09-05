/-
CLI wrapper around the VERIFIED path-certificate checker
(`Verify.PathCert.check`, soundness: `Verify.PathCert.check_sound`).

    verify <path-certificate-int.json>

Exit 0 iff the checker accepts. The JSON is the self-contained integer-scaled
certificate the pipeline writes next to the rational one; only the parsing
below and the Python integer scaling are unverified glue.
-/

import Lean.Data.Json
import Verify.PathCert
import Verify.DirPathCert

open Lean

def getInt! (j : Json) : Except String Int :=
  j.getInt?

def parseLabel (j : Json) : Except String (Option Int) :=
  match j with
  | Json.null => .ok none
  | _ => do pure (some (← j.getInt?))

def parseTriple (j : Json) : Except String (Nat × Nat × Int) := do
  let arr ← j.getArr?
  if h : arr.size = 3 then
    let a ← arr[0].getNat?
    let b ← arr[1].getNat?
    let c ← arr[2].getInt?
    pure (a, b, c)
  else
    .error "edge is not a 3-element array"

structure RawCert where
  source : Nat
  target : Nat
  claimed : Int
  labels : List (Option Int)
  path : List Nat
  edges : List (Nat × Nat × Int)
  directed : Bool

def parseCert (j : Json) : Except String RawCert := do
  let source ← (← j.getObjVal? "source").getNat?
  let target ← (← j.getObjVal? "target").getNat?
  let claimed ← (← j.getObjVal? "claimed_cost_int").getInt?
  let labels ← (← (← j.getObjVal? "labels_int").getArr?).toList.mapM parseLabel
  let path ← (← (← j.getObjVal? "path").getArr?).toList.mapM Json.getNat?
  let edges ← (← (← j.getObjVal? "edges_int").getArr?).toList.mapM parseTriple
  let directed := match j.getObjVal? "directed" with
    | .ok (Json.bool b) => b
    | _ => false
  pure { source, target, claimed, labels, path, edges, directed }

/-- Dispatch: the undirected checker (Verify.PathCert.check_sound) for lattice
paths, the directed one (Verify.DirPathCert.check_sound) for road driving times. -/
def runCheck (r : RawCert) : Bool × String :=
  if r.directed then
    let cert : Verify.DirPathCert.Cert :=
      { source := r.source, target := r.target, claimed := r.claimed, labels := r.labels,
        path := r.path, edges := r.edges.map fun (a, b, c) => { a, b, c } }
    (Verify.DirPathCert.check cert, "Verify.DirPathCert.check_sound")
  else
    let cert : Verify.PathCert.Cert :=
      { source := r.source, target := r.target, claimed := r.claimed, labels := r.labels,
        path := r.path, edges := r.edges.map fun (a, b, c) => { a, b, c } }
    (Verify.PathCert.check cert, "Verify.PathCert.check_sound")

def main (args : List String) : IO UInt32 := do
  match args with
  | [file] =>
    let text ← IO.FS.readFile file
    match Json.parse text >>= parseCert with
    | .error e =>
        IO.eprintln s!"parse error: {e}"
        pure 2
    | .ok cert =>
        let (ok, thm) := runCheck cert
        if ok then
          IO.println s!"OK: certificate verified by the Lean-proved checker (soundness: {thm})"
          pure 0
        else
          IO.eprintln "FAIL: certificate rejected"
          pure 1
  | _ =>
    IO.eprintln "usage: verify <path-certificate-int.json>"
    pure 2
