# Vendored rule sets

Reference material for the skills in `.claude/skills/`. Nothing here loads automatically — a
skill body points Claude at a specific file when it needs the depth.

**Source:** [ciembor/agent-rules-books](https://github.com/ciembor/agent-rules-books) v0.5,
commit `9c87636` (2026-05-22). MIT licensed, see `LICENSE`.

Each book directory holds two files:

- `<book>.mini.md` — the working version, ~30-45 rules. What a skill quotes from.
- `<book>.md` — the full canonical set, ~180-520 rules. For audits and deep sessions.

The upstream `nano` versions are deliberately not vendored. Their role — a compact always-on
baseline — is filled instead by `.claude/rules/engineering-baseline.md`, which merges all
fourteen sets into one conflict-resolved file rather than stacking fourteen competing ones.

`COMPATIBILITY.md` is the upstream matrix of which rule sets can be loaded together.
`.claude/rules/engineering-baseline.md` already encodes the rulings for the pairs that clash,
so consult the matrix only when adding a new book to this set.

These are practical engineering instructions inspired by the books, written for coding agents.
They are not official author or publisher materials and not a substitute for reading the books.
