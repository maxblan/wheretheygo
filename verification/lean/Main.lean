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

open Lean Verify.PathCert

def getInt! (j : Json) : Except String Int :=
  j.getInt?

def parseLabel (j : Json) : Except String (Option Int) :=
  match j with
  | Json.null => .ok none
  | _ => do pure (some (← j.getInt?))

def parseEdge (j : Json) : Except String Edge := do
  let arr ← j.getArr?
  if h : arr.size = 3 then
    let a ← arr[0].getNat?
    let b ← arr[1].getNat?
    let c ← arr[2].getInt?
    pure { a, b, c }
  else
    .error "edge is not a 3-element array"

def parseCert (j : Json) : Except String Cert := do
  let source ← (← j.getObjVal? "source").getNat?
  let target ← (← j.getObjVal? "target").getNat?
  let claimed ← (← j.getObjVal? "claimed_cost_int").getInt?
  let labels ← (← (← j.getObjVal? "labels_int").getArr?).toList.mapM parseLabel
  let path ← (← (← j.getObjVal? "path").getArr?).toList.mapM Json.getNat?
  let edges ← (← (← j.getObjVal? "edges_int").getArr?).toList.mapM parseEdge
  pure { source, target, claimed, labels, path, edges }

def main (args : List String) : IO UInt32 := do
  match args with
  | [file] =>
    let text ← IO.FS.readFile file
    match Json.parse text >>= parseCert with
    | .error e =>
        IO.eprintln s!"parse error: {e}"
        pure 2
    | .ok cert =>
        if check cert then
          IO.println "OK: certificate verified by the Lean-proved checker \
            (soundness: Verify.PathCert.check_sound)"
          pure 0
        else
          IO.eprintln "FAIL: certificate rejected"
          pure 1
  | _ =>
    IO.eprintln "usage: verify <path-certificate-int.json>"
    pure 2
