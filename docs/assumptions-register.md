# Annahmen-Register der Mod

Stand: Code vom 2026-09-04. Dieses Dokument ist auf Deutsch, weil es eine
Entscheidungsvorlage ist: **jede Zeile ist eine Annahme, die die Mod trifft, und die
du bestätigen, ändern oder verwerfen kannst.** Nichts hier ist eine Empfehlung —
es ist eine Bestandsaufnahme mit Herkunft und Wirkung.

Herkunfts-Kategorien:

- **Spiel** — spiegelt eine Formel oder Daten von Cities: Skylines II (dekompiliert).
- **Literatur** — durch peer-reviewte Quellen gestützt (`docs/scientific-model-review.md`).
- **Urteil** — Setzung des Mod-Autors, ohne externe Quelle; oft aus Beobachtung
  einzelner Städte (Valmare) abgeleitet.
- **Artefakt** — folgt aus der Implementierung, nicht aus einer fachlichen Absicht.
- **Nutzer** — Voreinstellung, die der Spieler ändern kann.

Verifikationsstand jeder Regel: `docs/correctness-claims.md`. Dieses Register sagt,
*was* gerechnet wird — nicht, ob es richtig gerechnet wird (das ist geprüft).

---

## 0. Grundmodell

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A0.1 | Nachfrage = reale Heim→Arbeit- und Heim→Schule-Paare aus dem Save | pro Bürger genau ein Ziel; Arbeit hat Vorrang vor Schule | Spiel | Keine Gravitationsschätzung; Freizeit-, Einkaufs-, Pendlerverkehr von außen fehlen | Sollen weitere Wegezwecke zählen? |
| A0.2 | Ausschlüsse | Touristen, Obdachlose, Haushalte ohne Wohnung | Urteil | Hotelgäste und Einpendler erzeugen keine Nachfrage | Gewollt? |
| A0.3 | Wegegewichte | Arbeit 1,0 · Schule 0,6 | Urteil (ohne Quelle) | Schulverkehr zählt 40 % weniger | Warum 0,6? Alternativ 1,0 oder empirisch? |
| A0.4 | Tagesprofil | Spitzenanteil 0,2 · Fahrten pro Weg 2 | Urteil | Bestimmt den Fahrgast-Floor je Fahrzeug (A6.4) | Plausibel für CS2-Tagesrhythmus? |
| A0.5 | Zonierung | 256-m-Zonen, Zonenzentrum als Ort; Zone→Haltestelle = nächste innerhalb 500 m (strikt <, Tie → niedrigerer Index) | Urteil / Artefakt | Nachfrage wird auf Zonenzentren kollabiert; Zonen ohne Haltestelle ≤ 500 m sind unsichtbar für das Routing | 256 m und 500 m sinnvoll? |
| A0.6 | Raster | Kacheln 32 m · Buckets 128 m · Lattice 128 m | Artefakt | Auflösung aller Karten und der freien Trassen | — |
| A0.7 | **Graphen ungerichtet** | Straßenkante = beide Richtungen; keine Abbiegeregeln | Artefakt (Modellgrenze) | Einbahnstraßen, Kreuzungskosten, Spurregeln fehlen | Akzeptabel für Busrouten? |
| A0.8 | Straßenkantenkosten | Bogenlänge, min 1 m; Autobahnen routbar, aber ohne Haltestellen | Urteil | Reisezeit ≈ Länge (keine Geschwindigkeit pro Straßentyp) | Straßenklassen-Geschwindigkeiten gewünscht? |
| A0.9 | Determinismus | Kein Zufall, keine Zeitabhängigkeit; zwei unspezifizierte Tie-Reihenfolgen bei exakt gleichen Scores | Artefakt | Reproduzierbar pro Runtime | — |

## 1. Heatmap (S1)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A1.1 ✅ umgesetzt (Phase 3) | Kern | linear fallend 1 − d/r (Dreieck), Einzugsradius r | Literatur-Analog (Hansen 1959) | Nähe zählt linear; kein Gauß, kein Plateau | Dreieck oder z. B. Gehzeit-basiert? |
| A1.2 ✅ umgesetzt (Phase 3: 6/11/16 min) | Einzugsradien | Bus 350 · Tram 450 · Metro 600 · Train 900 · Ferry 500 m | Urteil (Bus/Metro literaturkonform) | Was „im Einzugsbereich" heißt | Werte übernehmen? |
| A1.3 ✅ umgesetzt (Phase 3: Zeitkriterium) | Zugangsradien Straße | Bus 120 · Tram 130 · Metro 150 · Train 200 · Ferry 140 m | Urteil | Ab wann eine Kachel „erschlossen" ist | — |
| A1.4 ✅ umgesetzt (Phase 3) | Landmassen-Gating | Bevölkerung, Jobs, Zukunft nur aus derselben Landmasse (8-connected über **Land**, nicht Bebaubarkeit); Deckung/Umsteigen/Überlappung **nicht** gegated | Urteil (im Code begründet) | Kein Ziehen über Wasser; steile Hänge trennen nicht | Steile Hänge als Barriere gewollt? |
| A1.5 ✅ umgesetzt (Phase 3) | Zugänglichkeitsterm | 0,06 pro Kante + 0,15 pro Knoten, × (120/r)², saturiert auf 1 | Urteil (getuned) | Ein normales Raster saturiert | — |
| A1.6 ✅ umgesetzt (Phase 3) | Straßen-Gate | Score × sat(2·Zugang): unter 50 % Zugang fällt alles linear auf 0 | Urteil | Kacheln ohne Straße scoren nichts | Gewollt (Metro-Stationen ohne Straße?) |
| A1.7 | Normalisierung | pro Term 98. Perzentil der positiven Werte = 1; Deckung gekappt bei 1,5 | Urteil | Ausreißer beschneiden die Skala nicht | — |
| A1.8 | Gewichte W1–W7 | Bus 1,0/0,8/1,2/0,6/0,3/0,9/0,4 · Tram 1,0/0,9/1,3/0,7/0,35/0,8/0,45 · Metro 0,9/1,0/1,4/0,8/0,4/0,6/0,5 · Train 0,8/1,1/1,5/0,5/0,5/0,5/0,6 · Ferry 1,0/0,7/1,2/0,4/0,25/0,7/0,3 (Bedarf, Jobs, Deckung, Zugang, Zukunft, Umsteigen, Überlappung) | Nutzer / Urteil | Die eigentliche Standortbewertung | **Zentrale Frage: welche Kriterien und Gewichte willst du?** |
| A1.9 | Gewichtete Summe | Score = Σ wᵢ·Termᵢ | Urteil | Kann nicht-konvexe Pareto-Punkte nie erreichen (Das & Dennis 1997) | Alternativ lexikografisch / ε-Constraint? |
| A1.10 | Modus-Kapazitätsgewichte | Bus 1 · Ferry 1,2 · Tram 1,5 · Subway 2,5 · Train 3 (Helikopter 1, Schiff 1,5, Flugzeug 3) | Urteil | Ein Bahnhof „zählt" 3 Bushaltestellen | Passend zu CS2-Kapazitäten (Bus 80, U-Bahn 1080)? |
| A1.11 | Umsteige-Radius | min(250 m, Einzugsradius); innerhalb Bonus, außerhalb Überlappungs-Strafe (linear verblendet) | Urteil | Trennt Zubringer von Parallelverkehr | 250 m Umsteigeweg realistisch? |
| A1.12 | Gelände | Land = Wassertiefe ≤ 0,5 m; bebaubar = Land ∧ Neigung ≤ 15° (Nutzer 3–45°); Fähre: zusätzlich Ufer ≤ 0,6 m Tiefe, nur mit Wassernachbar | Urteil / Nutzer | Was als Standort infrage kommt | — |
| A1.13 | Fähren-Uferbonus | Bester Score im 5×5-Fenster + **1,0 pauschal** | Artefakt | Fährstandorte werden künstlich um 1 angehoben | Bewusst? |
| A1.14 | Kalibrierung | NNLS-Fit von W1/W2/W4/W5 gegen Little's-Law-Ankunftsrate (Warteschlange/Wartezeit); W3/W6/W7 nie gefittet; ≥ 8 Haltestellen × ≥ 30 Proben | Urteil | Vorschlag, nicht automatisch angewandt | — |

## 2. Standortauswahl (S2)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A2.1 | Kandidaten | nur 3×3-Lokalmaxima mit Score > 0 (Plateau: kleinster Index) | Artefakt | Schließt Nicht-Maxima aus, auch wenn sie in Summe besser wären | Gewollt? |
| A2.2 | Mindestabstand | max(2, round(Einzugsradius/32)) Kacheln, Chebyshev | Urteil | Bus: 11 Kacheln = 352 m | Chebyshev oder euklidisch? |
| A2.3 ✅ umgesetzt (Phase 2) | **Auswahlverfahren** | **Exakt** (Branch-and-Bound, `SuitabilityExactSites`): Max-Summe der Scores unter Mindestabstand, ≤ K Standorte; bei erschöpftem Suchbudget bestes Set + bewiesene Schranke, im Log ausgewiesen | Entscheidung | Auf allen 6 Instanzen (2 reale) bewiesen optimal, Gap 0 gegen SCIP/VIPR | Zielfunktion vorläufig Max-Summe; wird mit A1.8 zur Gerechtigkeits-Nebenbedingung (Phase 6) |
| A2.4 | Nachbewertung | Gehweg-Dijkstra (8-connected, Diagonale √2) über die Land-Maske, nur W1·Bedarf + W2·Jobs, linear fallend | Urteil | Rangfolge kann sich gegenüber Heatmap ändern | Auch Jobs/Zukunft/Deckung im Gehwegmodell? |
| A2.5 | Grenzen | ≤ 20 Standorte; Kandidatenpuffer 65 536 | Artefakt | — | — |

## 3. Nachfrage und Bedienungs-Abschlag (S3)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A3.1 | Abschlag für bereits bediente Wege | w ← w · sat(Tür-zu-Tür / Decke); Decke = min(3 × Median der bedienten Wege, 3600 s), Rückfall 3600 s bei < 20 bedienten Paaren | Urteil | Ein Weg, den das Netz in 1/3 der Medianzeit trägt, behält 1/3 seines Gewichts | Skalierung am Median sinnvoll? |
| A3.2 | Bedient heißt | erreichbar ∧ mindestens ein Fahrzeug benutzt ∧ Tür-zu-Tür ≤ 3600 s | Urteil | Reine Fußwege zählen nicht als Bedienung | — |
| A3.3 | Zuweisung auf das Netz | alles auf **einen** kürzesten Weg (alles-oder-nichts); Kostendeckel Straße 20 km, Lattice 30 km | Artefakt | Kein Gleichgewicht, keine Kapazität | Akzeptabel für Korridorsuche? |
| A3.4 | Fähren-Nachfrage | nur Paare auf verschiedenen Landmassen | Urteil | Fähre innerhalb einer Insel (Bucht) nie vorgeschlagen | Gewollt? |

## 4. Trassierung (S4)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A4.1 | Straßen: Korridorwachstum | Saat = stärkste unbenutzte Kante; Verlängerung nach Flow + Neuheit; Abbiegestrafe 0,6; Spreizung 0,7; ≤ 2 nachfragelose Knoten überbrückbar (× 0,05); Direktheit Ende/Ende ≥ 0,45 | Urteil | Heuristik ohne Zielfunktion | **Welches Ziel soll eine Straßenlinie eigentlich maximieren?** |
| A4.2 | Flow-Schwelle | 10 % des mittleren positiven Kantenflows; Nachfrage-Schwelle 0,005 | Urteil | — | — |
| A4.3 | Nach Annahme | Korridor „schält" 85 % seines Flows, Nachbarkanten 42,5 %; Neuheit 0,15 über 3 Sprünge | Urteil | Nächste Linie meidet den Korridor | — |
| A4.4 | Ziel-Umschalter | Fahrgäste / Ausgeglichen / Abdeckung = Neuheitsgewicht 0 / 1 / 4 × mittlerer Flow, Saat-Bias 0 / 0,5 / 1 | Urteil / Nutzer | Einzige Stellschraube der Zielfunktion | — |
| A4.5 | Lattices (Metro, Zug, Fähre) | direkter Kürzestweg zwischen den Enden des schwersten unbedienten Paares; Kosten: Zug 0,35 auf Gleis / 1,6 sonst, Metro 0,9 / 1,0, Wasser 1; **Steigung ignoriert** | Urteil | Freie Trassen; kein Flow-Wachstum | Gleisbevorzugung 4,6× für Zug gewollt? |
| A4.6 | Längenlimits | Straße 12 · Metro 15 · Zug 20 · Fähre 20 km | Urteil | — | — |
| A4.7 | Knotenpunkte | Enden auf Umsteigepunkt ≤ 250 m (Lattice) / ≤ 500 m Straßenweg (Straße); Biegung durch Hub ≤ 1,25 × direkt, Suchweite 2000 m, Snap 250 m, jeder 4. Knoten | Urteil | Linien werden zu Knoten hin verbogen | 25 % Umweg für einen Anschluss ok? |
| A4.8 | Kandidatenbudget | 4 × RouteCount je Netz, 8 × Versuche | Artefakt | Begrenzt den Pool, den S7 sieht | — |

## 5. Haltepunkte entlang einer Linie (S5)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A5.1 | Abstände | Bus 350 · Tram 450 · Metro 800 · Zug 2000 · Fähre 1200 m | Urteil (Bus, Metro literaturkonform; **Tram, Fähre ohne Quelle**) | Dichte der Halte | Werte übernehmen? Empirisch: Bus ≈ 350 gemessen, ≈ 400 optimiert; Metro ≈ 1100 gemessen |
| A5.2 | Gleichmäßige Intervalle | n = round(Länge/Abstand), Halte bei L·i/n | Urteil | Kein variabler Abstand (Theorie: optimal variiert entlang der Linie) | — |
| A5.3 | Verschiebung | ± 35 % des Intervalls entlang der Linie, 7 Stichproben; Mindestlücke 60 %; Station ≤ 150 m wird angefahren; Duplikat < 20 m verworfen; Endpunkte fest, wenn Score > 0 | Urteil | Halte suchen lokal den besten Score | — |
| A5.4 | Auslassen | Fenster unter 35 % des positiven Linien-Medians wird nicht bedient (Termini/Pflichthalte ausgenommen) | Urteil | Linie fährt durch, ohne zu halten | 35 % sinnvoll? |
| A5.5 | Flottenschätzung | 15 s Halt pro Stopp, min. 30 s Takt; Geschwindigkeiten Bus 9 · Tram 12 · Metro 18 · Zug 28 · Fähre 10 m/s | Urteil | Fahrzeuganzahl der Vorschläge | — |

## 6. Moduswahl (S6)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A6.1 | Mindestlängen | Bus 1400 · Tram 1800 · Metro 3200 · Zug 10 000 · Fähre 1200 m | Urteil (≈ 5 Halte) | Kürzeres wird nicht vorgeschlagen | — |
| A6.2 | Flow-Vielfache | Tram 1,5 · Metro 5 · Zug 8 · Fähre 1 · Bus 0 × mittlerer Kantenflow des Netzes | Urteil | Relative Kapazitätsschwellen | — |
| A6.3 | Reichweiten-Anteil | Zug 4 %, Metro 2 % der unbedienten Nachfrage; halbiert bei ≥ 60 % Gleisanteil | Urteil | Alternative zum Flow für Schienenmodi | — |
| A6.4 | Fahrgast-Floor | Fahrgäste ≥ Kapazität eines Fahrzeugs / (0,2 · 2); Bus ohne Floor | Spiel-Kapazitäten × Urteil (A0.4) | „Ein Zug muss sich füllen" | — |
| A6.5 | Kaskade | höchste Kapazität zuerst; Lattice-Trasse nur als der Modus, der sie gezeichnet hat; sonst Neu-Trassierung auf Straße | Urteil | Verhindert Zug-Trassen als „Metro" | — |
| A6.6 | Betrieb | Soll-Takte 300 / 240 / 200 / 480 / 600 s; Maximal-Längen 9 / 12 / 20 / 60 / 20 km (Bus/Tram/Metro/Zug/Fähre) | Urteil | Gesundheitsurteile, Splitting-Rat | — |

## 7. Routing, Kreditierung, Linienauswahl (S7)

| # | Annahme | Wert / Regel | Herkunft | Wirkung | Prüffrage |
|---|---|---|---|---|---|
| A7.1 | Transitgraph | Gehen 1,4 m/s; Umsteige-Fußweg ≤ 250 m; Einstieg = (Wartezeit + 5 s)/2 je Richtung (netto: Warten + 5 s pro Fahrzeug); Fahrt = spielinterne Dauer, sonst Distanz/Geschwindigkeit | Spiel (5 s, Wartemodell) · Urteil (250 m) | Modelliert den Spiel-Pathfinder | — |
| A7.2 | **Zeitgewichtung** | Gehen : Warten : Fahren = **1 : 1 : 1** | Spiel | Literatur: ≈ 2 : 2,5 : 1 (Wardman 2004) | Spieltreue oder Realismus? |
| A7.3 | Wartezeit | max(Takt/2, beobachtet) − Standzeit; Kandidaten: Bus 200 · Tram 180 · Metro 150 · Zug 300 · Fähre 400 s | Spiel · Urteil | Takt/2 empirisch nur bis ≈ 11 min haltbar | — |
| A7.4 | Kreditierung | Weg zählt für eine Linie, wenn er sie benutzt, ≥ 60 s schneller als vorher und ≤ 3600 s; Gewicht × 0,6^Umstiege (Nutzer: Transferstrafe 40 %) | Urteil / Nutzer | Direktfahrt schlägt Umsteigen | Literatur: Umstieg ≈ 5–18 Min. Äquivalent — Rabatt oder Zeitzuschlag? |
| A7.5 | **Auswahlverfahren** | Greedy-Runden: bester Kandidat je Runde, dann Neubewertung gegen erweitertes Netz; Bewertungsfenster RouteCount × 16; Reihenfolge Nachfrage ↓, Flow ↓ | Artefakt | Nachweislich nicht mengenoptimal (Zubringer-Gegenbeispiel: 40 statt 80) | **Optimal? Dann Zielfunktion der *Menge*: kreditierte Summe (aktuell gewählt) oder bediente Nachfrage?** |
| A7.6 | Annahme-Gates | Länge ≥ Modusminimum; Nachfrage > 0 ∨ Flow ≥ 25 % Referenz; Flow > 1; Nachfrage ≥ Bus-Floor; kein Duplikat (≥ 75 % der Halte ≤ 150 m an bestehender Linie) | Urteil | Filtert Vorschläge | — |
| A7.7 | Referenz | mittlerer positiver Kantenflow des Netzes; Lattice-Untergrenze 25 % der Straße | Urteil | Skaliert alle Flow-Schwellen | — |

## 8. Linien-Gesundheit (nicht Teil der Optimierung)

| # | Annahme | Wert / Regel | Herkunft |
|---|---|---|---|
| A8.1 | voll ≥ 85 % Auslastung; leer ≤ 6 % ∧ ≤ 35 % des Stadt-Medians ∧ Spitze ≤ 3× | Urteil |
| A8.2 | lange Wartezeit: ≥ 2 × eigener Solltakt ∧ ≥ 1,5 × Median ∧ ≥ 60 s | Urteil |
| A8.3 | Flotte = round(Umlaufzeit / Solltakt), Ziel-Auslastung 0,7 | Spiel · Urteil |

---

## Wo „optimal statt greedy" erreichbar ist — Vorab-Einschätzung

Voraussetzung überall: eine **festgelegte Zielfunktion**. A2.3, A4.1 und A7.5 haben heute keine.

| Stufe | Exakt erreichbar? | Wie | Rückfall |
|---|---|---|---|
| S2 Standorte | **Ja, praktisch sicher.** 716 Binärvariablen löste SCIP exakt in 0,36 s; im Mod (C#, kein Solver) per Branch-and-Bound über den geometrischen Konfliktgraphen — erwartbar Sekunden | Zielfunktion festlegen (Max-Score-Summe unter Mindestabstand; Anzahl K fest oder frei) | bestes gefundenes Set **mit bewiesener Schranke** (Gap ausgewiesen), wenn ein Zeitbudget reißt |
| S4 Lattice | **Bereits exakt** (Kürzestweg unter dem Kostenmodell) | — | — |
| S4 Straße | Erst definierbar, wenn ein Ziel steht; „max. Flow bei Längenlimit" ist längster-Pfad-artig, NP-hart | Ziel wählen | Greedy bleibt, ausgewiesen als Heuristik |
| S5 Halte je Linie | **Ja**, dynamische Programmierung pro Linie (1-D-Problem, Furth & Rahbee) | Ziel: Score-Summe unter Abstandsregeln | — |
| S7 Linienmenge | **Nur begrenzt.** NP-hart, keine Konstantfaktor-Garantie; exakt per Enumeration bis ≈ 10 000 Teilmengen (z. B. 39 Kandidaten bei K ≤ 3), 39 bei K = 5 sind 667 928 | Zielfunktion der Menge festlegen | Greedy + lokale Verbesserung (Tausch) + exakte Schranke aus Teil-Enumeration, klar als „nicht bewiesen optimal" markiert |
| Gesamtproblem S2 + S5 + S7 gemeinsam | **Nein** (Stand der Forschung: 15-Knoten-Benchmarks) | — | gestufte Optima mit ausgewiesenem Verlust (C7.5) |

Nächster Schritt liegt bei dir: markiere in diesem Register, welche Annahmen bleiben,
welche sich ändern sollen und welche Zielfunktionen für S2, S5 und S7 gelten sollen.
Danach ist „optimal" ein wohldefiniertes Ziel, und der Umbau kann beginnen.

---

## Entscheidungen (Review-Protokoll)

Stand 2026-09-04, Blöcke 0 und 1 durchgesehen. Unverändert übernommene Zeilen sind
nicht aufgeführt. „Folge" nennt, was sich im Modell daraus ergibt; Werte, die noch
zu belegen sind, verweisen auf die laufende Literaturrecherche (LR).

| # | Entscheidung | Folge / Stand |
|---|---|---|
| A0.1 ✅ umgesetzt (Phase 4: Shopping, Leisure, Relaxing, Sightseeing, VisitAttractions beobachtet; Fenster 1 Spieltag; Skalierung ≤ 4×; live bestätigt: 309 Wege nach 1,7 Spielstunden, alle Ziele lesbar) | Zusätzlich Freizeit und Einkauf; **keine Vorrangregel, alle Zwecke gleich gewichtet** | CS2 speichert Freizeit-/Einkaufsziele nicht am Bürger — nur die *laufende* Reise (`TravelPurpose` = Shopping/Leisure/…, Ziel in `Target`). Diese Nachfrage muss über die Zeit **beobachtet** werden (Reisen mit Zweck und Ziel aufsammeln, wie die Kalibrierung heute Haltestellen sammelt). Arbeit/Schule bleiben aus dem Save lesbar |
| A0.2 ✅ umgesetzt (Phase 1) | Alle Bewegungen innerhalb der Karte zählen (auch Touristen); Außenverbindungen erst einmal ignoriert | Tourist-/Obdachlosen-Filter entfällt; Reisen mit Ziel oder Quelle außerhalb der Karte werden verworfen |
| A0.3 ✅ umgesetzt (Phase 1) | Siehe A0.1: **alle Wegezwecke gleich = 1,0** | Schulfaktor 0,6 entfällt |
| A0.4 ✅ umgesetzt (Phase 6: Auslastungs-Floor 25 % ersetzt „ein voller Bus") | Rückfrage — erklärt in der Antwort; Entscheidung offen | Betrifft nur den Fahrgast-Floor je Fahrzeug (A6.4) |
| A0.5 | Unsicher; anderer Ansatz möglich | Vorschlag in der Antwort (Ziele nicht auf Zonenzentren kollabieren, sondern am Netz verorten) |
| A0.6 | Kleinere Auflösung erwogen; sinnvolle Werte gefragt | Vorschlag in der Antwort; Zahlen aus LR |
| A0.7 ✅ umgesetzt (Phase 5: Fahrspurrichtungen, Abbiegeklassen mit der Kurvenwinkel-Zeitkostenrate des Spiels; Spurwechselregeln nicht modelliert) | **Einbahnstraßen, Kreuzungskosten, Spurregeln müssen rein** | Straßengraph wird gerichtet, pro Fahrspur; Kostenmodell des Spiel-Pathfinders (`PathfindCosts`: Zeit/Verhalten/Geld/Komfort) als Vorlage |
| A0.8 ✅ umgesetzt (Phase 5: `CarLane.m_SpeedLimit` je Richtung, schnellste Spur) | **Straßenklassen-Geschwindigkeiten sind wichtig** | Datenquelle vorhanden: `Game.Net.CarLane.m_SpeedLimit` je Fahrspur |
| A0.9 | Bleibt | — |
| A1.1 ✅ umgesetzt (Phase 3) | **Zeitbasierte Bewertung** statt Luftlinie | Einzugsbereiche werden Gehzeit-Isochronen über das Fußwegenetz; Kern ist dann eine Zeitfunktion. Berechnung von den Quellen aus (≈ 5 000 Dijkstras) statt von jeder Kachel |
| A1.2 ✅ umgesetzt (Phase 3: 6/11/16 min) | Werte prüfen | LR: gemessene Gehdistanzen/-zeiten je Modus |
| A1.3 ✅ umgesetzt (Phase 3: Zeitkriterium) | Werte prüfen | LR; wird durch A1.1 zum Zeitkriterium |
| A1.4 ✅ umgesetzt (Phase 3) | **Nur Wasser trennt, Hänge nicht** (Bestand bestätigt) | Mit A1.1 implizit: erreichbar ist, was das Fußwegenetz erreicht |
| A1.5 ✅ umgesetzt (Phase 3) | Werte prüfen | Term wird mit A1.1/A1.6 neu definiert (Netzanbindung statt Straßendichte) |
| A1.6 ✅ umgesetzt (Phase 3) | Bestätigt: **Anbindung an Straße *oder Fußweg* nötig** | Heute zählen nur `Net.Road`-Kanten; `PedestrianLane`-Wege müssen dazu |
| A1.7 | Rückfrage — erklärt in der Antwort | — |
| A1.8 ✅ umgesetzt (Phase 6: Floor X = 80 % bedienter Wege in T = 10 min, zuerst Erschließungsgewinn, dann Effizienz; Gini als Kennzahl) | **Ziel: gleichmäßige, gerechte Verteilung der Nutzungschance** | Zielfunktion wechselt von „bester Standort" zu einem Gerechtigkeitsmaß über alle Bürger; Kandidatenmaße aus LR (Gini/Theil/Maximin) |
| A1.9 ✅ umgesetzt (Phase 6: ε-Constraint, lexikografisch) | Offen, fachlich sinnvollste Lösung | Mit A1.8: gewichtete Summe ungeeignet; ε-Constraint/lexikografisch, LR |
| A1.10 ✅ umgesetzt (Phase 1) | **Kapazitäten aus CS2 ableiten** | Ist heute nur für den Fahrgast-Floor so; die Heatmap-Modusgewichte (1/1,5/2,5/3) sind Setzungen → durch Kapazitätsverhältnisse aus den Prefabs ersetzen |
| A1.11 ✅ umgesetzt (Phase 1: 3 min ≈ 216 m; Zeitgewichtung 2,5 folgt in Phase 6) | Wert prüfen, eher kürzer | LR: akzeptable Umsteige-Gehzeit; wird Zeitkriterium |
| A1.12 | Bleibt | — |
| A1.13 ✅ umgesetzt (Phase 1) | **+1-Bonus entfällt** | Streichen |
| A1.14 | Unsicher | Zurückgestellt; hängt von A1.8 ab (Kalibrierung auf Nutzung ist ein Effizienz-, kein Gerechtigkeitsmaß) |

Blöcke 2–7, Rückmeldung vom 2026-09-05 (Antworten des Nutzers; „RF" = Rückfrage
gestellt, Entscheidung noch offen):

| # | Entscheidung | Folge / Stand |
|---|---|---|
| A2.1 ✅ umgesetzt (Phase 3) | **Nein — wirklich der beste Kandidat**, nicht nur 3×3-Lokalmaxima | Kandidatenmenge wird vollständig; mit Phase 3 sind die natürlichen Kandidaten die Netzknoten (Straße/Fußweg), nicht Kacheln. RF: Netzknoten als Kandidatenmenge? |
| A2.2 ✅ umgesetzt (Phase 3) | Idee: keine zwei Halte 5 m auseinander; Metrik unklar | RF mit Vorschlag: Mindestabstand als **Gehzeit im Fußwegenetz** = Haltabstand des Modus (A5.1) |
| A2.3 ✅ | Optimal | Phase 2 umgesetzt (B&B); wird mit A1.8/A4.1 zur Nebenbedingungs-Optimierung |
| A2.4 ✅ umgesetzt (Phase 3) | Frage unverstanden | Mit Phase 3 entfällt die Nachbewertung (Score ist bereits Netz-Gehzeit). RF: Streichen bestätigen |
| A2.5 | „Sinnvoll?" | 20 ist UI-Grenze, keine Algorithmusgrenze; RF |
| A3.1 | Vorschlag erbeten | Vorschlag: Abschlag entfällt; Zielfunktion „gesparte Personenzeit" (A4.1) enthält ihn implizit. RF: Basislinie = Gehen/Bestandsnetz, Auto ignoriert? |
| A3.2 ✅ | Reine Fußwege zählen nicht als Bedienung | bleibt |
| A3.3 | AON-Zuweisung ist nicht optimal | Vorschlag: AON nur zum Erzeugen von Kandidaten; Bewertung ausschließlich per Routing der gesamten Nachfrage über das Netz (S7). RF |
| A3.4 | **Nein — Fähren auch entlang einer Küste**, wenn Nachfrage da ist | Landmassenfilter entfällt; Fähre wird wie jeder Modus über die Zielfunktion bewertet |
| A4.1 | **Hauptziel: so viele Menschen wie möglich in so kurzer Zeit wie möglich; Auslastung hoch genug** | Zielfunktion = nachfragegewichtete Zeiteinsparung (Personenzeit); Nebenbedingung Auslastung ≥ X %. RF: X |
| A4.2 | „Sinnvoll?" | Vorschlag: Schwellen entfallen zugunsten der Auslastungs-Nebenbedingung. RF |
| A4.3 | **Keine Mehrfachlinien für dieselben Wege**; Kreuzen, Teilüberlappung, gleiche Endpunkte ok | Vorschlag: Duplikatregel über den Anteil gemeinsam bedienter Nachfragepaare, nicht über Halte ≤ 150 m; zusätzlich ergibt die Zielfunktion für eine Dublette ≈ 0 Nutzen. RF: Anteil |
| A4.4 | „Sinnvoll?" | Vorschlag: Umschalter entfällt; ersetzt durch Gerechtigkeits-Floor (T, X) + Auslastungs-Floor. RF |
| A4.5 | Nicht verstanden | Erklärung in der Antwort; Vorschlag: Zug bevorzugt Bestandsgleise (kostenlos), Metro ohne Steigung, Zug mit Steigungsgrenze. RF |
| A4.6 | „Sinnvoll?" | Vorschlag: Längenlimits → Fahrzeitlimits je Modus. RF |
| A4.7 | Wichtig, aber Ansatz fraglich: zählt der Umweg fürs Gesamtnetz? | Vorschlag: direkte und Hub-Variante beide als Kandidaten, exakte Mengenauswahl entscheidet. Kein Schwellenwert |
| A5.1 ✅ | Werte übernehmen | RF: sehr hohe Nachfrage unter Mindestabstand → Mindestabstand als harte Untergrenze 50 % des Nennwerts, DP entscheidet |
| A5.2 | Was optimaler ist | DP über Kandidatenpositionen (Phase 7) |
| A5.3 ✅ | gut | bleibt (als Kandidatenerzeugung für die DP) |
| A5.4 | Wert erfragt | Vorschlag: kein Prozentwert; Halt genau dann, wenn Zugangsgewinn der Einsteiger > Verzögerung der Durchfahrer. RF |
| A5.5 ✅ | **Werte aus dem Spiel** | Fahrzeug-Prefabs (Geschwindigkeit, Kapazität), Standzeit aus dem Spiel dekompilieren |
| A6.x | „Rest sinnvoll" | RF: Modus über Kapazitätsbedarf (Auslastung) statt Flow-Vielfache/Reichweiten-Anteile? |
| A7.2 | **Realismus** | Zeitgewichte Gehen 2,2 · Warten 2,1 · Umsteigen 2,5 · Fahren 1 (TCQSM). RF: Spielfiguren routen 1:1:1 — akzeptiert? |
| A7.4 | Literatur | Umstieg als Zeitäquivalent (≈ 5 min + gewichtete Wartezeit) statt 0,6-Rabatt |
| A7.5 | Optimal; unklar welche Zielfunktion | Vorschlag: Netzziel gemeinsam (Routing der gesamten Nachfrage über alle gewählten Linien), nicht Summe der Einzelkredite. RF |

**Antwort vom 2026-09-05: „Ja zu allem"**, mit drei Abweichungen — damit sind alle
Rückfragen oben entschieden (RF → ✅):

| # | Entscheidung |
|---|---|
| A1.8 / A4.1 | Gerechtigkeits-Floor **X = 80 %** (nicht 70 %), T = 10 min; Auslastungs-Floor 25 %; Rangfolge Gerechtigkeit → Auslastung → gesparte Personenzeit; Basislinie = Bestes aus Gehen und Bestandsnetz, Auto ignoriert |
| A4.5 | **Steigung komplett ignorieren** (auch für Zug); **Zug und Metro erhalten denselben Bonus** für bestehende Gleise |
| A7.2 | Realismus-Gewichte (2,2 / 2,1 / 2,5 / 1) werden **sichtbar gemacht** (Diagnose), **geplant wird mit den Spielwerten 1 : 1 : 1** |
| A2.1/A2.2 | Kandidaten = Netzknoten mit positivem Score; Mindestabstand = Gehzeit im Fußwegenetz ≥ Haltabstand des Modus |
| A2.4 | Nachbewertung entfällt |
| A2.5 | 20 bleibt UI-Grenze |
| A3.3 | AON nur zur Kandidatenerzeugung |
| A4.3 | Dublette bei > 50 % gemeinsam bedienter Nachfragepaare |
| A4.6/A6.1 | Fahrzeitgrenzen Bus 30 · Tram 35 · Metro 30 · Zug 60 · Fähre 45 min; Mindestlänge 3 Halte |
| A5.1/A5.4 | harte Untergrenze 50 % des Haltabstands; Halt genau dann, wenn Zugangsgewinn > Verzögerung der Durchfahrer |
| A6.x | Modus nach Kapazitätsbedarf bei Soll-Takt, Werte aus dem Spiel |
| Phase 3 | Einzugszeiten Bus/Tram 6 · Metro/Fähre 11 · Zug 16 min, linear fallend; Zugang 2 min zum Netzknoten; Bevölkerung je Wohngebäude; Kapazität/Überfüllung ignoriert |

---

## Vorschläge aus der Literaturrecherche (2026-09-04) für die offenen Werte

Alle Zahlen mit Primärquelle in `docs/scientific-model-review.md` §10; „V" = im
Primärtext gelesen.

| Offen | Belegte Grundlage | Vorschlag |
|---|---|---|
| A1.1/A1.2 Einzugsbereich als Gehzeit | Planungsstandard 400 m Bus / 800 m Schiene ≙ 5 / 10 min bei 5 km/h (TCQSM, V). Gemessen (Montreal, Netzdistanz, 85. Perzentil): Bus 484 m, Metro 873 m, Regionalzug 1 259 m (El-Geneidy et al. 2014, V); Sydney Median Bus 364 m, Zug 749 m (Daniels & Mulley 2013, V). Euklidische Kreise überschätzen die Abdeckung um bis zu 57 % (Gutiérrez & García-Palomares 2008) | **Gehzeit über das Netz, kein Radius.** Bus/Tram: 6 min (≈ 480 m bei 1,34 m/s), Metro: 11 min (≈ 880 m), Zug: 16 min (≈ 1 260 m), Fähre wie Metro (kein eigener Beleg). Abklingfunktion statt harter Kante — TCQSM und Zhao et al. 2003 raten beide dazu |
| Gehgeschwindigkeit | TCQSM 1,2 m/s (1,0 m/s bei ≥ 20 % Ältere), HCM-Grundlage 1,2 m/s (FHWA 1998), Bohannon 1997 komfortabel 1,27–1,46 m/s | **1,2 m/s** als Planungswert; heute 1,4 m/s im Routing (A7.1) → auf 1,2 senken |
| A1.11 Umsteige-Fußweg | „2–3 min" ist **nirgends belegt**; belegt sind Zeitgewichte: Umsteigezeit ≈ 2,5 × Fahrzeit (TCQSM Exhibit 4-5, V); reine Umsteigestrafe ≈ 4,9 min Fahrzeit-Äquivalent U-Bahn/U-Bahn (Guo & Wilson 2011) | Umsteigen als **Zeit mit Gewicht 2,5**, nicht als Radius; als Suchradius für Umsteigekanten 3 min ≈ 200 m — bewusst kürzer als heute (250 m) |
| A0.6 Raster | Feinstes Raster in der Literatur 30 m (El-Geneidy 2014); Hexagone ≈ 66 m Kante (Vale & Lopes 2023); 100–250 m üblich; MAUP-Warnung: Werte hängen von der Zellgröße ab | **32 m beibehalten** — feiner als alles Belegte; bei Netzzeit ist das Raster nur Anzeige |
| A1.5 Zugänglichkeitsterm | kein Literaturwert für „0,06/0,15" — der Term ist eine Setzung | Mit A1.1 **ersetzen** durch: Kachel ist erschlossen, wenn ein Netzknoten (Straße oder Fußweg) innerhalb T_zugang Gehzeit liegt; kein Dichtemaß mehr |
| Umwegfaktor (falls je ohne Netz gerechnet) | Raster 1,42 worst case; gemessen 1,18–1,24 (TCQSM; O'Sullivan & Morrall 1996; Levinson & El-Geneidy 2009) | irrelevant, sobald über das Netz gerechnet wird |

### A1.8 / A1.9 — Gerechtigkeitsmaße zur Auswahl

„Vergleichbare Chance für alle, unabhängig vom Ziel" lässt sich fachlich auf vier Arten
fassen; sie belohnen Verschiedenes, und die Wahl ist eine **Wertentscheidung**:

| Maß | Was es misst | Belohnt / bestraft | Quelle |
|---|---|---|---|
| **Mindeststandard (sufficientarisch)** | Anteil der Bürger, deren Wohnung *und* Ziel innerhalb T Gehminuten einer Haltestelle liegen (optional: mit ÖV-Fahrzeit ≤ T₂) | belohnt, jeden über die Schwelle zu heben; gleichgültig darüber | Pereira, Schwanen & Banister 2017; Lucas, van Wee & Maat 2016; Martens 2017 |
| **Maximin / Leximin (Rawls)** | die Erreichbarkeit des schlechtest gestellten Bürgers, dann des zweitschlechtesten … | belohnt nur den Boden; kann Effizienz stark kosten (Preis bis 11 % bei 2 Parteien, Bertsimas et al. 2011) | Martens 2012; Martens, Golub & Robinson 2012 |
| **Gini / Lorenz** (Delbosc & Currie: Melbourne G = 0,68) | Ungleichheit der Erreichbarkeit über alle Bürger | bestraft Konzentration; **skaleninvariant** — halbiert man allen den Zugang, bleibt G gleich → nur als Diagnose geeignet | Delbosc & Currie 2011 |
| **Theil / Atkinson** | wie Gini, zerlegbar nach Gruppen (Theil) bzw. mit einstellbarer Aversion (Atkinson: ε → ∞ = Rawls) | wie oben; Theil sagt, ob die Lücke *zwischen* Vierteln oder *in* ihnen liegt | Theil 1967; Atkinson 1970 |

Empfehlung der Recherche (mit der ich übereinstimme): **Mindeststandard als harte
Nebenbedingung** (ε-Constraint: „mindestens X % der Bürger erreichen …", Mavrotas 2009 für
das Verfahren) und darunter die vorhandene Effizienz-Zielfunktion; Gini/Theil als
ausgewiesene Diagnose, nicht als Ziel. Damit wird A1.9 beantwortet: nicht gewichtete
Summe, sondern ε-Constraint — und für S2/S7 ergibt sich daraus eine wohldefinierte
Zielfunktion, gegen die „optimal" beweisbar ist.
