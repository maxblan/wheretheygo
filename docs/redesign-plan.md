# Umbauplan: von der Heuristik zur zeitbasierten, gerechten, beweisbar optimalen Planung

Stand 2026-09-05 (Phasen 1–6 und Phase 7 Stufe 1 umgesetzt; 1–2 und die Standortauswahl aus 3 auf der Realstadt zertifiziert; die Heatmap-Terme aus 3 warten auf einen Export mit dem Build, der die Gleitkomma-Arithmetik festnagelt). Grundlage: die Entscheidungen im `docs/assumptions-register.md`
(Blöcke 0–1 entschieden, 2–7 in Durchsicht) und die Zusage „optimal, wo beweisbar;
sonst bestmöglich mit ausgewiesener Schranke". Jede Phase ist für sich baubar
(`make strict`), getestet und durch `verification/` prüfbar; die Spezifikation
(`docs/formal-specification.md`) wird pro Phase fortgeschrieben, die alte bleibt als
v1 dokumentiert und verifiziert.

| Phase | Inhalt | Entscheidungen | Verifikation |
|---|---|---|---|
| **1 Konstanten & Bereinigung** ✅ (2026-09-04) | Wegezwecke gleich gewichtet, Touristenfilter weg; Gehgeschwindigkeit 1,2 m/s; Umsteige-Fußweg 3 min (216 m) statt 250 m; Fähren-+1 gestrichen; Modusgewichte aus CS2-Fahrzeugkapazitäten statt Tabelle | A0.2, A0.3, A1.10, A1.11, A1.13, Gehgeschwindigkeit | bestehende Instanzen (Konstanten sind Daten), Golden-Tests, Export v1 bleibt ladbar |
| **2 Exakte Standortauswahl (S2)** ✅ (2026-09-05) | Branch-and-Bound über den geometrischen Konfliktgraphen im Mod: Max-Score-Summe unter Mindestabstand; bewiesen optimal oder bestes Set + Schranke bei Zeitbudget | A2.3 (Zielfunktion vorläufig Max-Summe; wird mit A1.8 zur Gerechtigkeits-Nebenbedingung) | Pipeline zertifiziert das Mod-Optimum gegen SCIP/VIPR — Gap muss 0 sein |
| **3 Fußwegenetz & Zeit** ✅ (2026-09-05; Realstadt-Zertifikat steht bis zum nächsten Export aus) | Fußwegegraph (Straßen + `PedestrianLane`); Gehzeit-Isochronen von den Quellen; Einzugsbereiche als Minuten (Bus/Tram 6, Metro 11, Zug 16, Fähre 11) mit Abklingfunktion; Zugänglichkeit = Netzknoten in Zugangs-Gehzeit; Nachfrage an Netzknoten statt 256-m-Zonen | A1.1, A1.2, A1.4, A1.5, A1.6, A0.5, A0.6 | neuer Burst-Job ⇒ neue `heatmap_grid`-Spezifikation und Evaluator; Export v2 |
| **4 Beobachtete Nachfrage** ✅ (2026-09-05) | Einkaufs-/Freizeitwege aus laufenden Reisen (`TravelPurpose`, `Target`) sammeln, mit Arbeit/Schule zusammenführen; Vollständigkeitsanzeige | A0.1 | Enumerations-/Routing-Instanzen aus Export |
| **5 Gerichteter Straßengraph** ✅ (2026-09-05; Realstadt-Zertifikat steht bis zum nächsten Export aus) | Fahrspuren, Einbahnrichtung, `m_SpeedLimit`, Abbiegekosten nach dem Kostenmodell des Spiel-Pathfinders | A0.7, A0.8 | Kürzestweg-Zertifikate auf gerichteten Graphen (Lean-Checker erweitern) |
| **6 Gerechtigkeit als Ziel** ✅ (2026-09-05; Realstadt-Nachweis steht bis zum nächsten Export aus) | Mindeststandard als ε-Constraint (Anteil der Bürger mit Wohnung *und* Ziel in T Gehminuten einer bedienten Haltestelle), darunter Effizienz; Gini/Theil als Kennzahl | A1.8, A1.9; T, X als Einstellungen | Enumeration/MIP mit der neuen Zielfunktion |
| **7 Exakte Halte & begrenzt exakte Linien** — Stufe 1 ✅ (2026-09-05): Linienset exakt | Stufe 1: S7 als Branch-and-Bound über Teilmengen unter „gesparte Personenzeit" (lexikographisch nach gekapptem Gerechtigkeitsanteil), Auslastungs- und Duplikat-Nebenbedingung auf dem Set, Knotenbudget → Optimum oder bestes Set + Obergrenze; Kredit-/Greedy-Runden, Transferstrafe, Flow-Gates entfernt. **Offen**: DP je Linie (S5, A5.1/A5.4), Modus nach Kapazitätsbedarf (A6.x), Fahrzeitgrenzen (A4.6/A6.1), direkte + Hub-Variante als Kandidaten (A4.7), Landmassenfilter der Fähren (A3.4), Nachfrage an Netzknoten fürs Routing (A0.5) | A7.5, A4.1, A3.1, A3.3, A4.2–A4.4, A7.2, A7.4 ✅; A5.x, A6.x, A4.6, A4.7, A3.4 offen | `lineset_time`: Subjekt = Mod-Code, exakter Evaluator, vollständige/beschränkte Enumeration; 5/5 synthetisch Gap 0; Realstadt wartet auf Export |

Reihenfolge: 1 → 2 → 3 → 4 → 5 → 6 → 7. Phasen 2 und 3 sind unabhängig voneinander;
6 setzt 3 voraus (Gehzeit), 7 setzt 6 voraus (Zielfunktion). Offene Entscheidungen
der Blöcke 2–7 des Registers fließen in 5–7 ein.
