# Release plan — Transit Architect

Tracking list towards the first public release. One line per item, ticked when done and
verified; "verified how" names the evidence. Decisions that are still the author's are marked
**RF** (Rückfrage) and link to the question they wait on. Kept current by whoever closes an item.

Status vocabulary: `[ ]` open · `[~]` in progress · `[x]` done and verified.

## 1. Correctness (docs/correctness-claims.md is the authority)

- [x] S1–S9 specified, verified offline, Lean CF.1–CF.10 (2026-09-06)
- [x] Real-city three-way checks: heatmap, sites, roads, coverage, stops, health (Valmare 2026-09-06)
- [~] Real-city `-lineset` v3: the 2026-09-06 run timed out again even with the enumeration bounded to
      the empty set (`ENUM_BUDGET=1`), leaving only `solution.json` and `subject.log`. Faithfulness and
      optimality on a real city stay open until the evaluator's shortest-path search is faster — pure-Python
      `Fraction` Dijkstra over 19 858 door pairs is the bottleneck, and parallelising the check phase was
      not enough
- [ ] Re-export after the schedule guard (`Daytime.Advise`) and check `-health` three-way again (C9.10)
- [ ] Log checklist of OPEN-GAPS.md on the next run: metro on metro track only, staged updates,
      tunnel nodes, C1.13, C6.3, C6.4, C8.5, C9.11 (slider ends vs the panel)
- [x] **A6.9 v2** — a rung below the line's mode only qualifies while it needs no more vehicles than run today (decided 1a, 2026-09-06)
- [x] **A6.10** — the busier period sizes the fleet and no period may exceed the ceiling (decided 3a); applies to suggestions and existing lines
- [x] **A8.10** — lines with fewer than two stops inside the city are ignored entirely (outside connections stay outside the model)
- [x] **A8.9** — both periods under the floor is not a schedule question (`Daytime.Advise`)
- [ ] Deploy the three changes and re-verify on a real city (`-health`, `-coverage` is a fresh baseline)
- [ ] Rewrite OPEN-GAPS.md after the log run (current edition is 2026-09-05 evening)
- [ ] Performance note for weak PCs: route pass ~31 s on the worker, set search hits its 15 s budget and is
      not proven optimal on Valmare (ceiling 434 h vs 158 h found) — release note or a setting

## 2. UI / UX — built 2026-09-06, see docs/ui-architecture.md for the mechanics

Goal (author): trivially easy to use, every setting in the game's Options page, the rest
indistinguishable from vanilla. Decisions: 6a, 7c, 8b, 9b, 10a (+ colour buildings by transit
access), 11a, 12a, 13a, 14a, 15a, 16a, 17a, 18a, 19a.

- [x] Options page in two sections, "General" and "Advanced" (8b); everything the panel duplicated moved there
- [x] Route objective to Advanced, default Balanced (9b)
- [x] Infoview in vanilla style: the game draws its own gradient legend for a `GradientInfomodeBasePrefab` (10a)
- [x] Colour buildings by how well they are connected (10a) — `BuildingAccessColorSystem` writes
      `Game.Objects.Color` between `ObjectColorSystem` and `BatchDataSystem`
- [x] Suggestions and line health in the vanilla Transport Overview (7c, 12a, 15a): one extra cell per
      line row via `extend(TransportLineItem)`, the suggestions as a section under the list
- [x] One-click actions: focus a line, set the fleet and the schedule through the game's own policies (13a, 17a)
- [x] Game notification for ModeUp, SplitRoute, Remove (16a) via `IconCommandSystem`
- [x] The `MutationObserver` hack is deleted; our infoview row now simply belongs in the game's menu
- [x] Localisation EN and DE for every key the module uses; the two files carry identical key sets (18a)
- [x] Payload contracts kept (`|`-delimited rows, `AddUpdateBinding`) and the purity rule held
- [x] Site markers with rank (11a): rings sized and faded by rank, in a gold that is no
      mode's colour. **Not clickable** — the overlay buffer draws geometry only and a click
      needs a tool of its own
- [x] Mod icon and thumbnail in the game's style (19a) — the toolbar button and infoview icon ship; the Steam thumbnail is block 3
- [x] First in-game run reviewed and every finding fixed (2026-09-06 13:17 screenshots): system
      ordering, the slider bars, the overview column, the terrain alpha, the untranslated status
      lines, the panel chrome. See docs/ui-architecture.md for the two root causes
- [ ] Choose the notification icon prefab from the names the run logged (`s_LineNotificationIconName`
      is empty, so the rebuild markers are off). The save offers 160; "Passenger Transport" is the
      likely one
- [ ] Second in-game run: verify the note rows, the suggestions section, the building colours (the
      log now says in one line whether it is colouring and why not), the site rings, both locales

## 3. Repository hygiene

- [x] The stale `Mods/StationSuitabilityOverlay` deployment is deleted — it was loading beside the
      renamed mod, so the game ran two copies of it at once (2026-09-06)
- [ ] Remove or relocate `example-mods/` (three third-party mods) — keep only a note of what was learnt from them
- [ ] `PublishConfiguration.xml`: description still describes the v1 heat map (Bus 350 m / Metro 600 m, road density); rewrite for v3
- [ ] Thumbnail.png reviewed
- [ ] `.gitignore` covers `verification/runs`, `verification/instances/real-*`, `.verify-toolchain`, `verification/.toolchain`
- [ ] `CHANGELOG.md` started (this release = 1.0.0)
- [ ] Repository name, solution and assembly consistent (`TransitArchitect`) — done 2026-09-06; check docs still name the old id where it matters (log file name changes with the assembly name)

## 4. Community files (.github/)

- [ ] `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1)
- [ ] `CONTRIBUTING.md`: build requirements (Windows toolchain, `CSII_TOOLPATH`), `make strict`, the purity rule, how to add a claim
- [ ] `SECURITY.md`
- [ ] `.github/ISSUE_TEMPLATE/bug_report.md` (asks for `TransitArchitect.Mod.log` and the save), `feature_request.md`
- [ ] `.github/PULL_REQUEST_TEMPLATE.md` (checklist: `make strict`, harness test added, claim/register updated)
- [ ] `.github/CODEOWNERS` (optional)

## 5. Continuous integration (.github/workflows/)

The game build needs the CS2 modding toolchain on Windows; GitHub runners do not have it.

- [ ] `offline.yml`: harness (`dotnet run --project tests/TransitArchitect.Tests`), `dotnet format --verify-no-changes`
      for the tests, `node --check` on the UI module, subject build, `python3 run.py` over the synthetic
      instances that need no solver (skip `sites*`), Python evaluators' self-tests
- [ ] `verify.yml` (optional, slower): conda SCIP 10 exact + viprcomp/viprchk with cache, `sites*` instances; Lean 4.15 build
- [ ] Mod build: document as local-only (`make strict`) or a self-hosted Windows runner with the toolchain
- [ ] Badges in README

## 6. Documentation for players

- [ ] Player README (what the mod shows, how to build a suggestion, what each verdict means, the Options)
- [ ] Developer docs stay: CLAUDE.md, docs/*, verification/README.md
- [ ] Release notes for 1.0.0 (known limits: A8.7 routing ignores schedules, A8.10, performance note)

## 7. Release mechanics

- [ ] Version number and `ModId` in `PublishConfiguration.xml`
- [ ] Publish profile tested once with a private upload
- [ ] Tag `v1.0.0`, GitHub release with the changelog
