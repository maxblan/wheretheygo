---
paths:
  - "crates/socha_protocol/**"
  - "tests/fixtures/**"
  - "bindings/**"
  - "xtask/**"
---

# Versioned contracts

Everything here crosses a process, language, or version boundary. Treat it as a published
contract, not as internal shape.

- Add fields and variants. Never repurpose, renumber, or silently reinterpret an existing one.
  A protobuf field number is permanent; an enum discriminant that changes meaning corrupts stored
  replays.
- A rename is a wire change, not a refactoring. `/refactoring-pass` does not cover it.
- Plan the mixed-version cases explicitly: old reader with new writer, new reader with old data,
  in-flight messages, and replays recorded by a previous build.
- One fact, one owner. A change to the wire format means updating, in the same change: the
  `.proto`, the Rust encode/decode, `tests/fixtures/cross_language_contract.json`, and the Rust,
  Python, and Java parsers that each hand-parse that fixture. Each language parses it by hand, so
  a new command or event variant needs a new match arm in all three.
- A season's `.proto` is different: its Java and Python classes are *generated*. Change one and run
  `cargo run -p xtask -- codegen`, then commit what it writes. Never hand-edit anything under
  `bindings/java/socha-java/src/generated/` or `bindings/python/socha_py/python/socha/**/v1/`.
- Every season payload on the wire carries the `game_id` that names its decoder, derived from the
  payload itself. Do not add a path that decodes one without a label: protobuf bytes are not
  self-describing, and the wrong decoder yields a plausible position rather than an error.
- `PROTOCOL_VERSION`, `REPLAY_FORMAT_VERSION`, and the golden files under `xtask/tests/fixtures/`
  are pins, not conveniences. Never regenerate a golden file to make a change pass; if it moved,
  find out why. Bumping `REPLAY_FORMAT_VERSION` requires deciding what happens to replays on disk.
- The same no-weakening rule covers the two sides `cargo test --workspace` does not run:
  `bindings/python/socha_py/tests/test_cross_language_contract.py` and
  `bindings/java/socha-java/src/test/java/de/software_challenge/socha/CrossLanguageContractTest.java`.
  Verify with `./scripts/validate-release validate`, not with workspace tests.
- prost output is generated into `OUT_DIR` and `include!`d — never edited, never committed.
- Bindings are thin facades over `crates/socha-client-core` (ADR 0003). Logic added here instead
  of the core will drift across three languages.
- Generated code is not committed but CI runs `git diff --exit-code`: anything a build step
  produces into the tree must already be up to date.
