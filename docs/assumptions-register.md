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
*was* gerechnet wird — nicht, ob es richtig gerechnet wird (das ist geprüft). Jede
Zahl, die hier steht, steht im Code genau einmal: `dotnet/Common/Planning/Assumptions.cs`
(Nutzerregel 2026-09-06).

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
| A1.12 ✅ umgesetzt (2026-09-06) | **Das Fußwegenetz wird zusammengefügt, wo die Spieldaten es zerschneiden** | Das Netz nimmt eine Kante je Straßensegment mit Fußweglane. Ein Segment ohne Gehweg mitten in einer Straße trennt aber alles dahinter ab: Valmare hatte 3845 Knoten in **582 Teilen**, das größte mit 46 %. Der Median-Abstand eines Teils zum Hauptnetz war 34 m — dieselben Straßen, an einem Segment zerschnitten. Nur 769 von 3845 Knoten lagen im 10-Minuten-Gehweg einer bedienten Haltestelle. Deshalb: je zwei Komponenten, deren nächstes Knotenpaar ≤ 50 m auseinanderliegt, werden mit einer Kante verbunden, bewertet mit Luftlinie ÷ Gehgeschwindigkeit. Ergebnis auf Valmare: 579 Brücken, 3 Komponenten, 2477 statt 769 Knoten im Horizont. 50 m, weil man so weit ohne Gehweg geradeaus geht — weiter, und der Graph steigt über Kanäle |
| A1.4 ✅ umgesetzt (Phase 3) | Landmassen-Gating | Bevölkerung, Jobs, Zukunft nur aus derselben Landmasse (8-connected über **Land**, nicht Bebaubarkeit); Deckung/Umsteigen/Überlappung **nicht** gegated | Urteil (im Code begründet) | Kein Ziehen über Wasser; steile Hänge trennen nicht | Steile Hänge als Barriere gewollt? |
| A1.5 ✅ umgesetzt (Phase 3) | Zugänglichkeitsterm | v1: 0,06 pro Kante + 0,15 pro Knoten, × (120/r)², saturiert auf 1. v2: Gehzeit-Kern 1 − t/2 min zum nächsten Gehwegknoten | Urteil (getuned) | Ein normales Raster saturiert | — |
| A1.6 ✅ v2 (2026-09-05 abends) | Zugang als Rabatt | v1: Score × sat(2·Zugang). v2 (Phase 3): W4·Zugang **additiv** — jede Kachel binnen 2 min eines Gehwegs leuchtete. **v3 (Nutzer, Option A): Score = sat(1 − W4·(1 − Zugang)) × (übrige Terme)**; bei W4 = 1 Knotenscore × Kern, bei W4 = 0 Weg frei; ein Gehweg ohne erreichbare Bewohner/Jobs/Umstiege = 0 bei jedem W4 | Nutzer 2026-09-05 („1: A") | Untergrund- und Nirvana-Straßen leuchten nicht mehr; W4-Slider heißt jetzt „Rabatt Erreichbarkeit" | — |
| A1.15 ✅ (2026-09-05 abends) | Tunnel und Brücken | Knoten, an dem jeder Gehweg mit `CompositionFlags.General.Tunnel` oder `.Elevated` ankommt (`NetCompositionData`, Spielklassifikation), ist **begehbar, aber kein Standort**: Quellen snappen auf alle Knoten, Kacheln und Standortkandidaten nur auf Bodenknoten; ein Bodengehweg genügt (Portal, Brückenkopf); `Side.Raised/Lowered` gilt als Boden | Nutzer 2026-09-05 („2: b, 3: gleich") | Kachel über einem Tunnel liest den nächsten Bodengehweg oder nichts | Wird `Elevated` vom Spiel erst ab Brückenhöhe gesetzt? (Log zählt die Knoten) |
| A1.16 ✅ (2026-09-05 abends) | Zukunftsterm nur neben Menschen | T5 zählt nur, wo am Knoten Bewohner (T1 > 0) oder Jobs (T2 > 0) erreichbar sind; reine Zonierung = 0 | Nutzer 2026-09-05 („5: Nein — keine Travels, keine Linie") | Leere gezonte Straßen leuchten nicht; Linien waren nie betroffen (nur Reisen erzeugen Linien) | Term ganz streichen (W5 = 0 als Default)? |
| A1.7 | Normalisierung | pro Term 98. Perzentil der positiven Werte = 1; Deckung gekappt bei 1,5 | Urteil | Ausreißer beschneiden die Skala nicht | — |
| A1.8 | Gewichte W1–W7 | Bus 1,0/0,8/1,2/0,6/0,3/0,9/0,4 · Tram 1,0/0,9/1,3/0,7/0,35/0,8/0,45 · Metro 0,9/1,0/1,4/0,8/0,4/0,6/0,5 · Train 0,8/1,1/1,5/0,5/0,5/0,5/0,6 · Ferry 1,0/0,7/1,2/0,4/0,25/0,7/0,3 (Bedarf, Jobs, Deckung, Zugang, Zukunft, Umsteigen, Überlappung) | Nutzer / Urteil | Die eigentliche Standortbewertung | **Zentrale Frage: welche Kriterien und Gewichte willst du?** |
| A1.9 | Gewichtete Summe | Score = Σ wᵢ·Termᵢ | Urteil | Kann nicht-konvexe Pareto-Punkte nie erreichen (Das & Dennis 1997) | Alternativ lexikografisch / ε-Constraint? |
| A1.10 | Modus-Kapazitätsgewichte | Bus 1 · Ferry 1,2 · Tram 1,5 · Subway 2,5 · Train 3 (Helikopter 1, Schiff 1,5, Flugzeug 3) | Urteil | Ein Bahnhof „zählt" 3 Bushaltestellen | Passend zu CS2-Kapazitäten (Bus 80, U-Bahn 1080)? |
| A1.11 | Umsteige-Radius | min(250 m, Einzugsradius); innerhalb Bonus, außerhalb Überlappungs-Strafe (linear verblendet) | Urteil | Trennt Zubringer von Parallelverkehr | 250 m Umsteigeweg realistisch? |
| A1.12 | Gelände | Land = Wassertiefe ≤ 0,5 m; bebaubar = Land ∧ Neigung ≤ 15° (Nutzer 3–45°); Fähre: zusätzlich Ufer ≤ 0,6 m Tiefe, nur mit Wassernachbar | Urteil / Nutzer | Was als Standort infrage kommt | — |
| A1.13 | Fähren-Uferbonus | Bester Score im 5×5-Fenster + **1,0 pauschal** | Artefakt | Fährstandorte werden künstlich um 1 angehoben | Bewusst? |
| A1.14 v2 | Kalibrierung | NNLS-Fit von **W1/W2/W5** gegen Little's-Law-Ankunftsrate auf den Regressoren Rabatt·sat(T1), Rabatt·sat(T2), Rabatt·T5' unter dem aktuellen W4 (Fit-Modell ≡ Score-Modell, Harness-Test); **W4 nicht gefittet** (am Halt ist der Kern ≈ 1: Achsenabschnitt, keine Steigung); Serien des alten Modells werden beim Laden verworfen (`model=`-Stempel) | Urteil | Vorschlag, nicht automatisch angewandt | — |

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
| A4.5 | Lattices (Metro, Zug, Fähre) | direkter Kürzestweg zwischen den Enden des schwersten unbedienten Paares; Kosten: Zug 0,35 auf Gleis / 1,6 sonst, Metro 0,9 / 1,0, Wasser 1; **Steigung ignoriert**. **v2 (2026-09-05 abends): zwei Schienengitter** — Zuggleis und Metrogleis nach `TrackLaneData.m_TrackTypes` getrennt, jedes Gitter bevorzugt nur seine eigene Gleisart (Bonus 0,35 auf Bestandsgleis für beide) | Urteil · Spielfakt (Gleisart) | Freie Trassen; kein Flow-Wachstum; keine Metro auf Zuggleis | Gleisbevorzugung 4,6× gewollt? |
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
| A6.7 | Leiter über Netze | Eine Schienentrasse wird zuerst auf der Straße nachgezogen; trägt Bus oder Tram ihre eigenen Fahrgäste unter 100 % und besteht die Formgates, ist DAS der Kandidat und die Schienentrasse entfällt. Schiene bleibt nur ohne Straßenweg, bei überlasteter Straße oder wenn die Straßenvariante die Gates reißt; Fähre behält das Wasser und bekommt die Straßenvariante nur unter dem Floor | Urteil (Befund 19:44: drei Metros 1,4–5,4 km ohne Bestandslinien) | „Der kleinere Modus zuerst" gilt auch zwischen Netzen | — |
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

## 8. Linien-Gesundheit (S9; v2 seit 2026-09-06, Antworten des Nutzers auf 31 Fragen)

| # | Annahme | Wert / Regel | Herkunft | Wirkung |
|---|---|---|---|---|
| A8.1 | Leer nur relativ zur Stadt | leer ⇔ mittlere Belegung ≤ min(6 %, 0,35 × oberer Median der Belegungen) ∧ Spitze ≤ 3 × diese Schranke; oberer Median = Wert an Index n/2 der sortierten Liste | Urteil (Werte) · **Lean:** höchstens n/2 Linien können unter der Schranke liegen | Ein Signal von zwei für „umleiten oder entfernen“; allein löst es nichts aus |
| A8.2 | Nachfrage einer Bestandslinie = Fahrgäste, die das Routing der bekannten Wege über das heutige Netz ihr zuordnet (`LineSetEvaluation.BaseRiders`, nach Tag/Nacht gesplittet), aus dem Basislauf jedes Routen-Passes | dieselbe Routing-Regel wie S7, kein zweites Modell (Frage 30a) | Spiel/Modell | Bis zum ersten Pass „keine Nachfrage bekannt“: dann kein Entfernen, kein Fahrplan-Rat, Flotte nur aus der Last |
| A8.3 | Planungslast = 90.-Perzentil (nearest rank) der Fahrgäste an Bord über die aktiven Lesungen des Fensters; Flotte für die Last = ⌈Last ÷ (0,7 × Plätze je Fahrzeug)⌉; Zielauslastung 0,7 | Urteil (0,7; Frage 4b/13a) · **Lean:** minimale Flotte | Maximum wird gespeichert und angezeigt, bemisst aber nichts |
| A8.4 | Lesungen ohne Fahrzeuge (Kapazität 0: inaktive Linie) zählen in keinem Mittel; Fenster 1 Spieltag, ≥ 4 aktive Lesungen, sonst die Momentaufnahme | Frage 10b/7a | Nur-Tag-Linien werden nachts nicht als leer gemessen |
| A8.5 | Lesung alle 15 Spielminuten (262144/96 Frames) auf dem Simulationstakt, unabhängig von der Spielgeschwindigkeit; Pause fügt nichts hinzu | Frage 6b/27a | 96 Lesungen je Spieltag |
| A8.6 | Urteilsreihenfolge: Flotte unter Soll (Spielflag) → Modus hoch → Route teilen → Entfernen (nur beide Signale) → Modus runter → Flotte hoch → Flotte runter → Fahrplan → gesund | Frage 22a/26a/16a · **Lean:** Reihenfolge und Zwei-Signal-Regel | „Überfüllt“ des Spiels heißt nur: das Spiel bekommt die Fahrzeuge nicht, die der Spieler eingestellt hat |
| A8.7 | Fahrplan-Rat nach den Perioden-Auslastungen (Nachfrage je Periode bei der empfohlenen Flotte) gegen die 15 % wie bei Vorschlägen; das Routing kennt keinen Fahrplan (Nachtfahrgäste einer Nur-Tag-Linie sind die, die das Netz tragen WÜRDE) | Frage 17b | Grenze dokumentiert, nicht modelliert |
| A8.8 (Befund) | `TransportLine.m_VehicleInterval` ist kein gemessener Takt: min(10 × Soll, Fahrzeit ÷ Soll-Flotte); auf fahrenden Linien < 1,5 × Soll, auf inaktiven die ganze Fahrzeit | Spiel (dekompiliert 2026-09-06) | Das v1-Urteil „lange Wartezeit“ (≥ 2 ×) war auf fahrenden Linien unerreichbar und ist gestrichen (Frage 25a) |
| A8.9 (Guard, 2026-09-06 nach dem ersten Spiellauf) | Beide Perioden unter der Untergrenze sind keine Fahrplanfrage: die Bestandslinie behält ihren Fahrplan (`Daytime.Advise`); mit Nachfrage in beiden Perioden wird eine Nur-Tag-Linie auf ganztags beraten | Befund Valmare: drei Zuglinien ohne geroutete Fahrgäste wurden auf „ganztags“ beraten | — |
| A8.10 ✅ entschieden 2026-09-06 | **Linien mit weniger als zwei Halten in der Stadt werden ganz ignoriert** (weder beurteilt noch befahren noch als bediente Halte gezählt); ein Halt ist außerhalb, wenn `Game.Objects.OutsideConnection` an ihm oder in seiner `Owner`-Kette hängt | Nutzer 2026-09-06: „die Züge bedienen nur Außenverbindungen, und die Mod ignoriert diese erstmal völlig“ | Erklärt den Befund (0 geroutete Fahrgäste bei 6–16 % Belegung); eine Regionallinie mit Stadtbahnhöfen bleibt vollständig erhalten; die ignorierten Linien stehen im Log |
| A6.9 v2 ✅ entschieden 2026-09-06 (Frage 1a) | **Modus runter nur, wenn die kleinere Stufe nicht mehr Fahrzeuge braucht als heute fahren.** Nach oben bleibt es bei „die Maximalflotte trägt die Last nicht“ (Frage 2a) | Urteil | Behebt den Befund: fünf von sechs Straßenbahnen sollten zu 6–12 Bussen werden |
| A6.10 ✅ entschieden 2026-09-06 (Frage 3a) | **Die stärkere Periode bemisst die Flotte:** Flotte = max(ganzer Tag, Tagperiode, Nachtperiode), jede Periode mit ihrem Anteil der Sitze (16/24 bzw. 8/24); eine Menge/Linie ist überlastet, wenn eine der drei Auslastungen über der Obergrenze liegt | Urteil | Behebt den Befund „Tagesauslastung 129 % bei Tagesmittel 98 %“; gilt für Vorschläge (S6/S7) und Bestand (S9) gleich |
| A6.8 (neu) | **Die Fahrzeugspanne des Spiels ist die Taktbedingung** (Frage 31a): Flotte = kleinste Zahl, deren Sitze am Tag die Einsteiger nicht über die Obergrenze (100 %) füllen, geklemmt in die Spanne, die der Fahrzeug-Regler für die Umlaufzeit erlaubt (Policy-Prefab: Modus und Spanne des `VehicleInterval`-Modifikators auf dem Prefab-Intervall); Intervall = Umlauf ÷ Flotte; gilt für Vorschläge und Bestand | Spiel | Die Planungstakt-Tabelle 300/240/200/480/600 s entfällt; A5.5 geschlossen |
| A6.9 (neu) | Leiter für Bestandslinien = Leiter des Netzes der Linie (`NetworkOf`): erster Modus, dessen Maximalflotte die geforderte Flotte trägt; darüber „Route teilen“; darunter „Modus runter“ (Frage 14a, 15a: Kilometer-Tabelle `MaxSensibleLength` und die Ein-Stufe-Regel `NextModeUp/Down` gestrichen) | Urteil | Bus↔Tram wechseln, Metro/Zug/Fähre haben eine Stufe |

## Wo „optimal statt greedy" erreichbar ist — Vorab-Einschätzung

Voraussetzung überall: eine **festgelegte Zielfunktion**. A2.3, A4.1 und A7.5 haben heute keine.

| Stufe | Exakt erreichbar? | Wie | Rückfall |
|---|---|---|---|
| S2 Standorte | **Ja, praktisch sicher.** 716 Binärvariablen löste SCIP exakt in 0,36 s; im Mod (C#, kein Solver) per Branch-and-Bound über den geometrischen Konfliktgraphen — erwartbar Sekunden | Zielfunktion festlegen (Max-Score-Summe unter Mindestabstand; Anzahl K fest oder frei) | bestes gefundenes Set **mit bewiesener Schranke** (Gap ausgewiesen), wenn ein Zeitbudget reißt |
| S4 Lattice | **Bereits exakt** (Kürzestweg unter dem Kostenmodell) | — | — |
| S4 Straße | Erst definierbar, wenn ein Ziel steht; „max. Flow bei Längenlimit" ist längster-Pfad-artig, NP-hart | Ziel wählen | Greedy bleibt, ausgewiesen als Heuristik |
| S5 Halte je Linie | **Ja**, dynamische Programmierung pro Linie (1-D-Problem, Furth & Rahbee) | Ziel: Score-Summe unter Abstandsregeln | — |
| S7 Linienmenge | **Nur begrenzt.** NP-hart, keine Konstantfaktor-Garantie; exakt per Enumeration bis ≈ 10 000 Teilmengen (z. B. 39 Kandidaten bei K ≤ 3), 39 bei K = 5 sind 667 928 | Zielfunktion der Menge festlegen | Greedy + lokale Verbesserung (Tausch) + exakte Schranke aus Teil-Enumeration, klar als „nicht bewiesen optimal" markiert | **Stand 2026-09-05: umgesetzt wie vorhergesagt** — exakt bis ≈ 3 Linien, darüber Greedy + Tausch-Lokalsuche mit ausgewiesener Obergrenze (A7.5) |
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
| A0.4 ✅ entschieden 2026-09-05: Auslastungs-Floor **15 %** = Einsteiger je Spieltag ÷ angebotene Plätze je Spieltag (Spieltag = 4 369 Bewegungssekunden). Zur Einordnung: die Momentanbesetzung der bestehenden Linien liegt im Median bei 4,1 %; bester Valmare-Kandidat 20,5 % | Rückfrage — erklärt in der Antwort; Entscheidung offen | Betrifft nur den Fahrgast-Floor je Fahrzeug (A6.4) |
| A0.5 ✅ umgesetzt (Phase 3 Heatmap, Phase 7 Routing) | Unsicher; anderer Ansatz möglich | Vorschlag in der Antwort (Ziele nicht auf Zonenzentren kollabieren, sondern am Netz verorten) | → Die Mengenauswahl routet jede Reise Tür zu Tür (Ursprungs- und Zielposition der Reise), nicht zwischen 256-m-Zonenzentren; Zonen bleiben nur für die Kandidatenerzeugung (AON) |
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
| A2.5 ✅ entschieden | „Sinnvoll?" | 20 ist UI-Grenze, keine Algorithmusgrenze — bleibt |
| A3.1 ✅ umgesetzt (Phase 7) | Vorschlag erbeten | Abschlag entfällt in der Auswahl: die Zielfunktion „gesparte Personenzeit" (A4.1) enthält ihn implizit; Basislinie = Bestes aus Gehen und Bestandsnetz, Auto ignoriert (`LineSet.Evaluate`). Der Abschlag bleibt nur noch für die Kandidaten-Erzeugung (AON-Zuweisung, A3.3) |
| A3.2 ✅ | Reine Fußwege zählen nicht als Bedienung | bleibt |
| A3.3 ✅ umgesetzt (Phase 7) | AON-Zuweisung ist nicht optimal | AON nur zum Erzeugen von Kandidaten; die Auswahl bewertet ausschließlich per Routing der gesamten Nachfrage über Bestandsnetz ∪ gewählte Linien (Spez. §6.2–6.3) |
| A3.4 ✅ umgesetzt (Phase 7) | **Nein — Fähren auch entlang einer Küste**, wenn Nachfrage da ist | Landmassenfilter entfernt (`BuildCrossWaterFlows` weg): das Wasser-Gitter sieht jede Reise; die Fähre wird über gesparte Zeit und Auslastung beurteilt wie jeder Modus |
| A4.1 ✅ umgesetzt (Phase 7) | **Hauptziel: so viele Menschen wie möglich in so kurzer Zeit wie möglich; Auslastung hoch genug** | Zielfunktion = Σ w · (vorher − nachher) Tür-zu-Tür, lexikographisch nach dem gekappten Gerechtigkeitsanteil; Nebenbedingung Auslastung ≥ 15 % je Linie, auf die Fahrgäste des Sets bezogen (Spez. §6.3) |
| A4.2 ✅ umgesetzt (Phase 7) | „Sinnvoll?" | Flow-Schwellen (Evidenz-Gate, Korridorflow > 1, Ein-Bus-Nachfrage) entfallen; nur noch Auslastungs-Floor und Duplikatregel |
| A4.3 ✅ umgesetzt (Phase 7) | **Keine Mehrfachlinien für dieselben Wege**; Kreuzen, Teilüberlappung, gleiche Endpunkte ok | Vorschlag: Duplikatregel über den Anteil gemeinsam bedienter Nachfragepaare, nicht über Halte ≤ 150 m; zusätzlich ergibt die Zielfunktion für eine Dublette ≈ 0 Nutzen. RF: Anteil |
| A4.4 ✅ umgesetzt (Phase 7) | „Sinnvoll?" | Die Auswahl kennt keinen Umschalter mehr: Gerechtigkeits-Floor (T, X) → gesparte Personenzeit. `RouteGoal` wirkt nur noch auf das Korridorwachstum (Kandidatenerzeugung) |
| A4.5 ✅ umgesetzt (Phase 7) | Nicht verstanden | Steigung wird nirgends betrachtet; EIN Schienengitter mit dem Gleisbonus des Zugs (0,35 auf Bestandsgleis, 1,6 sonst) für Metro und Zug gleich; die Fahrgäste entscheiden zwischen beiden (A6.x) |
| A4.6 ✅ umgesetzt (Phase 7) | „Sinnvoll?" | Längenlimits → Fahrzeitlimits: Bus 30 · Tram 35 · Metro 30 · Zug 60 · Fähre 45 min (`MaxRideSecondsFor`), Fahrzeit = gerichtete Straßenlegs bzw. Reisegeschwindigkeit + (Halte − 2)·δ; Mindestlänge = 3 Halte |
| A4.7 ✅ umgesetzt (2026-09-05) | Wichtig, aber Ansatz fraglich: zählt der Umweg fürs Gesamtnetz? | Direkte und Hub-Variante gehen beide als Kandidaten in die exakte Mengenauswahl (`BuildDirectForNetwork` → `TraceCandidate`); die 1,25×-Schranke bleibt nur als Zulässigkeit der Hub-Variante. Varianten einer Trasse (direkt/Hub, Modus/nächster Modus, Schiene/Straßen-Retrace) bilden eine Gruppe, aus der ein Set höchstens eine wählt (`LineCandidate.Group`). Auslöser: eine U-Bahn, die 700 m zu einem Fähranleger ins Feld und zurück gebogen wurde (Valmare, 12:25) |
| A5.1 ✅ | Werte übernehmen | RF: sehr hohe Nachfrage unter Mindestabstand → Mindestabstand als harte Untergrenze 50 % des Nennwerts, DP entscheidet |
| A5.2 ✅ umgesetzt (Phase 7) | Was optimaler ist | Exakte DP über Kandidatenpositionen alle 50 m (`StopPlanning`), Zielfunktion Zugangsgewinn − Durchfahrerverzögerung, Untergrenze 50 % des Haltabstands (A5.1) |
| A5.3 ✅ | gut | bleibt (als Kandidatenerzeugung für die DP) |
| A5.6 ✅ umgesetzt (2026-09-06) | Ein Halt an einer bestehenden Haltestelle braucht keine eigene Nachfrage | Ein Kandidat innerhalb von 150 m einer bedienten Haltestelle ist zulässig, auch wenn der Eignungswert dort 0 ist (nicht auf dem Wasser). Ohne das wurde die Trasse hinter den letzten bewerteten Kandidaten zurückgeschnitten — genau von dem Umsteigepunkt herunter, auf den `AimEndsAtInterchange` sie gelegt hatte (55 von 93 Enden auf Valmare) |
| A5.4 ✅ umgesetzt (Phase 7) | Wert erfragt | Kein Prozentwert: Halt genau dann, wenn Σ w·max(0, H − Gehzeit) der Einsteiger die Verzögerung Durchfahrer·δ übersteigt; δ = Standzeit + v/2a + v/2b aus den Prefabs (A5.5) |
| A5.5 ✅ (mit RF) | **Werte aus dem Spiel** | Gelesen: Kapazität, Beschleunigung/Bremsen (`CarData`/`TrainData`/`WatercraftData`), Standzeit und Standardintervall (`TransportLineData`). **Befund Valmare 16:51: Intervall Bus/Tram 45 s, Metro 60 s, Zug/Fähre 90 s; Standzeit 1–10 s; Beschleunigung 5–6 m/s².** Damit wäre der 15-%-Floor unerreichbar (257 Fahrgäste = 3 % statt 29 %) und ein Halt fast kostenlos (alle 175 m). Umgesetzt: Planungstakt = Tabelle 300/240/200/480/600 s, δ = max(Physik, 15 s); Prefab-Werte stehen im Log. **RF geschlossen 2026-09-06 (Frage 31a): die Fahrzeugspanne des Spiels ist die Taktbedingung, die Tabelle entfällt (A6.8).** |
| A6.x ✅ umgesetzt (Phase 7) | „Rest sinnvoll" | Modus = kleinster der Netzleiter, dessen Fahrzeuge beim Planungstakt nicht überlastet sind (Auslastung ≤ 100 %), gemessen an den eigenen Fahrgästen nach Neuplatzierung der Halte; die nächste Stufe wird zusätzlich als Kandidat angeboten und das Set kennt eine Auslastungs-Obergrenze von 100 % (Befund 17:06: Bus mit 154 % gewählt); Flow-Vielfache, Reichweiten-Anteile, Gleis-Erleichterung und Ein-Bus-Floor gestrichen; Werte aus `TransportLineData`, `PublicTransportVehicleData`, `CarData`/`TrainData`/`WatercraftData` |
| A7.2 ✅ umgesetzt (Phase 7) | **Realismus** | Geplant wird mit 1 : 1 : 1; die TCQSM-gewichtete Zeit (2,2 / 2,1 / 1) wird aus denselben Wegen als Diagnose je Set ausgegeben (`LineSetEvaluation.Walk/Wait/RideSeconds`) |
| A7.4 ✅ umgesetzt (Phase 7) | Literatur | Der 0,6-Rabatt und die Einstellung „Transferstrafe" sind gestrichen; ein Umstieg kostet genau seinen Fußweg und die Wartezeit der nächsten Linie, wie im Spiel |
| A7.5 ✅ umgesetzt (Phase 7; Verfahren 2026-09-05 abends erweitert) | Optimal; unklar welche Zielfunktion | Netzziel gemeinsam über `LineSet.Solve` in drei Stufen: Greedy-Aufbau → Tausch-Lokalsuche → exakte Branch-and-Bound-Suche vom gefundenen Set aus (monotone Vereinigungsschranke, Gruppen-Ausschluss, Eltern-Schranke). Bewiesen optimal, wenn die Suche im Budget schließt (Valmare bei 3 Linien: ja, 1,3 s); sonst bestes Set + Obergrenze, und der Nutzer sieht im Log „best found, NOT proven optimal“. **Nutzerentscheidung 2026-09-05: annähernd optimal ist akzeptabel, wenn die Laufzeit sinkt** → Budget 30 s, Pass höchstens alle 300 s, Bewertung türfrei und parallel auf halben Kernen. Messung Export 17:59 (K = 5, 68 Kandidaten): Lokalsuche 441,7 k s·Wege/Tag in ≈ 3 s; exakte Suche allein (60 s im Spiel) 422,0 k; Build davor 305,1 k; Obergrenze 630,6 k, Suche schließt nicht. Literatur: exakte Modelle skalieren nicht (Borndörfer/Grötschel/Pfetsch 2007; „Line Planning at Scale“ 2026), Nachbarschaftssuche erreicht Best-known (TRNDP-Review 2019, VNS 2022); Greedy-Garantie 1 − e^(−γ) nur bei Submodularität (Das/Kempe), hier durch Zubringer-Komplementarität nicht gegeben. Pipeline `lineset_time`: 7 synthetische Instanzen mit vollständiger Enumeration, Gap 0 |

**Antwort vom 2026-09-05: „Ja zu allem"**, mit drei Abweichungen — damit sind alle
Rückfragen oben entschieden (RF → ✅):

| # | Entscheidung |
|---|---|
| A1.8 / A4.1 | Gerechtigkeits-Floor **X = 80 %** (nicht 70 %), T = 10 min; Auslastungs-Floor 25 % → nach Korrektur des Spieltags (4369 Bewegungssekunden) und Online-Recherche am 2026-09-05 auf **15 %** gesetzt („Ja, mache 15%"); Rangfolge Gerechtigkeit → Auslastung → gesparte Personenzeit; Basislinie = Bestes aus Gehen und Bestandsnetz, Auto ignoriert |
| A4.5 | **Steigung komplett ignorieren** (auch für Zug); **Zug und Metro erhalten denselben Bonus** für bestehende Gleise — seit dem Abend jedoch jeweils nur für die **eigene Gleisart** (Nutzer 19:44: „eine Metro kann nicht auf normalen Zugschienen fahren") |
| A7.2 | Realismus-Gewichte (2,2 / 2,1 / 2,5 / 1) werden **sichtbar gemacht** (Diagnose), **geplant wird mit den Spielwerten 1 : 1 : 1** |
| A2.1/A2.2 | Kandidaten = Netzknoten mit positivem Score; Mindestabstand = Gehzeit im Fußwegenetz ≥ Haltabstand des Modus |
| A2.4 | Nachbewertung entfällt |
| A2.5 | 20 bleibt UI-Grenze |
| A3.3 | AON nur zur Kandidatenerzeugung |
| A4.3 ✅ umgesetzt (Phase 7) | Dublette bei ≥ 50 % der eigenen Fahrgäste, die ohne die Linie nicht langsamer wären (`DuplicateShare`); die 150-m-Halteregel ist gestrichen |
| A4.6/A6.1 | Fahrzeitgrenzen Bus 30 · Tram 35 · Metro 30 · Zug 60 · Fähre 45 min; Mindestlänge 3 Halte |
| A5.1/A5.4 | harte Untergrenze 50 % des Haltabstands; Halt genau dann, wenn Zugangsgewinn > Verzögerung der Durchfahrer |
| A6.x | Modus nach Kapazitätsbedarf bei Soll-Takt, Werte aus dem Spiel |
| Phase 3 | Einzugszeiten Bus/Tram 6 · Metro/Fähre 11 · Zug 16 min, linear fallend; Zugang 2 min zum Netzknoten; Bevölkerung je Wohngebäude; Kapazität/Überfüllung ignoriert |

**Nachträge vom 2026-09-05 (Performance, Spieltests 12:25 / 17:06 / 17:15 / 17:58):**

| # | Entscheidung / Befund | Folge |
|---|---|---|
| Laufzeit | Nutzer: „Kartoffel-PC“-Ziel, zunächst ohne Qualitätsverlust; später: „annähernd optimal ist akzeptabel, wenn die Laufzeit sinkt“ | Routen-Pipeline auf einem Worker; Hauptthread gesperrt, solange er läuft; Hauptthread je Refresh 64 ms (vorher 174–336, davor Sekunden-Freeze) |
| A7.5 Verfahren | s. o.: Greedy → Tausch → exakt, Budget 30 s, Pass alle 300 s, Bewertung türfrei (Mehrquellen-Dijkstra über Haltestellen) und parallel auf halben Kernen; Gruppen (Varianten einer Trasse schließen sich aus) | Pass ≈ 15–20 s statt 114 s; bessere Sets |
| A5.5 Prefab-Werte | Befund: Intervalle 45/45/60/90/90 s, Standzeit 1–10 s, Beschleunigung 5–6 m/s² | Planungstakt = Tabelle 300/240/200/480/600 s, δ = max(Physik, 15 s); **RF geschlossen 2026-09-06: Spielspanne statt Tabelle (A6.8)** |
| A6.x Obergrenze | Befund 17:06: Bus mit 154 % gewählt (Modus an fremder Variante gemessen) | Modus an eigenen Fahrgästen nach Neusetzen der Halte; nächste Stufe als Variante; Set-Obergrenze 100 % |
| A0.5 Paare | Reisen Tür zu Tür statt Zonenzentren; Türen sind in der Wegesuche Senken (kein Fußweg durch eine Haustür) | Semantik im Spec §6.2 v2; Evaluator angepasst |
| Warmstart | Nutzer 2026-09-05: „eigenes Savefile, damit kein Cold Start nach jedem Laden“ | Vorschläge, beobachtete Wege und Linien-Messungen werden über `IDefaultSerializable` in den Spielstand geschrieben (Format-Version, längenpräfixierter Payload; Laden ohne Mod überspringt den Block). Erster Pass nach dem Laden wartet den normalen Takt |
| Restlaufzeit | Budget 15 s; Paartabelle und Basislinie einmal je Pass statt je Variante (Modusauflösung 8 s → erwartet < 1 s) | Messung im nächsten Spiellauf |
| A4.5 v2 / A6.7 | Befund 19:44 (Valmare, fast alle Linien entfernt): drei Metros von 1,4–5,4 km, eine davon auf Zuggleis | Ursache 1: EIN Schienengitter mit Zug-Gleisbonus, die Metro lief auf Zuggleis → zwei Gitter nach `m_TrackTypes`. Ursache 2: die Leiter begann je Netz, eine Schienentrasse gewann gegen die Straße auf Geschwindigkeit → Straßenvariante zuerst, Schiene nur bei überlasteter Straße, fehlendem Straßenweg oder gerissenen Gates. **Im nächsten Spiellauf am Log zu prüfen** („streets overloaded as a …" / „the Metro alignment stands") |
| A1.6 v3 / A1.15 / A1.16 | Nutzer 2026-09-05 (Screenshot): Untergrundstraßen und Straßen „im Nirvana" leuchten gelb | Ursache: W4·Zugang additiv, jeder Gehwegknoten binnen 2 min zählte. Entscheidungen des Nutzers: Zugang als Multiplikator (A); Tunnel-/Brückenknoten begehbar, kein Standort (b, auch für Brücken); Nirvana damit erledigt; reine Zonierung zählt nicht. Umgesetzt in `SuitabilityScoring.Combine` (rein, getestet), `WalkGraph.Siteable`/`SnapSite`, `SuitabilityInputs.EndOnGround`; Export-Feld `node_siteable`; Plumbing-Instanz mit Brückenknoten dreiseitig bit-exakt; alte Exporte lesen sich als „alle Standorte" und bleiben unverändert grün. Kalibrierung auf drei Regressoren umgestellt, damit Fit- und Score-Modell dieselbe Formel sind. **Offen: Haltepunkte einer Straßenlinie im Tunnel** — der Haltepunktplan arbeitet auf dem Straßengraphen und kennt die Gleisart/Höhe noch nicht |
| A9.1 Aktualisierung | Nutzer 2026-09-05: Vorschläge sollen auch ohne Heatmap aktualisieren; die Liste darf sich nicht unter der Auswahl zurücksetzen, das Panel meldet ein Update und der Nutzer übernimmt | Zugangs-, Nachfrage- und Routen-Pass laufen unabhängig vom gezeichneten Overlay (nur das Malen hängt an „aktiv"); ein fertiger Pass wird **gestaged** (`routeUpdate`-Binding = Anzahl, Button „übernehmen" → `applyRouteUpdate`), sofort übernommen nur bei leerer Liste; ein neuerer Pass ersetzt einen wartenden. Nebenwirkung: der Verifikations-Export erscheint jetzt auch bei ausgeschalteter Heatmap |
| A8 Betriebszeiten | Nutzer 2026-09-05: Vorschläge und Linienzustand sollen sagen, ob eine Linie tags, nachts oder ganztags fahren soll | Spielfakten dekompiliert: Nacht = 22:00–06:00 (`TransportLineSystem`), Policies `RouteOption.Day/Night`; Schichten auf `EconomyParameterData.m_WorkDayStart/End` (+8 h Abend, +16 h Nacht), Schüler wie Tagschicht, Bürger-Offset ±1 h nicht modelliert. Empfehlung: nur tags, wenn die Nacht unter dem Auslastungs-Floor liegt und der Tag nicht (spiegelbildlich nur nachts); Bestandslinien nach Perioden-Mittel des Fensters mit ≥ 4 Messungen je Periode und nur bei ganztägigem Betrieb. **Entscheidung offen: soll die Mengenauswahl den Fahrplan mitoptimieren (Sitze nur in der Betriebsperiode)?** Heute ist er eine Empfehlung nach der Auswahl |

**Entscheidungen vom 2026-09-06 (Linienzustand, 31 Fragen; alle Antworten liegen im Chat-Protokoll vor):**

| # | Entscheidung | Folge |
|---|---|---|
| 1b/22a | Beweisziel: Treue (dreiwege-bitgenau) UND Zielfunktion der Empfehlung: kleinster Modus der Leiter, dessen Maximalflotte die Last trägt; Flotte minimal unter Last- und Nachfragebedingung in der Spielspanne | §7e; Lean `Verify.LineHealth` |
| 2a | Arithmetik aus `Lines.Judge` in den reinen Teil (`LineHealthRules.JudgeAll`) | Subject kann urteilen |
| 3c | Belegung (gemessen) trägt die Leer-Regel, Nachfrage-Auslastung (Routing) das zweite Signal und den Fahrplan | A8.1/A8.2 |
| 4b | 90.-Perzentil als Planungslast, Maximum daneben | A8.3 |
| 5a | oberer Median | A8.1, Lean |
| 6b/27a | Lesungen in Spielzeit, alle 15 Spielminuten | A8.5 |
| 7a, 8a, 9a→25a | Fenster/Mindestlesungen bleiben; Leer-Schwellen bleiben; Wartezeit-Urteil gestrichen (Befund A8.8) | — |
| 10b | inaktive Lesungen raus | A8.4 |
| 11a/12a | Spieler setzt eine Anzahl; Empfehlung nennt eine Anzahl in der Spielspanne, das Intervall nur zur Anzeige | §5 v3 |
| 13a/23a | Flotte = max(⌈Last⌉, ⌈Nachfrage⌉) mit ⌈⌉ statt round | A8.3, A6.8 |
| 14a/15a/16a | S6-Leiter für Bestand; Kilometer-Tabelle weg; Entfernen nur mit zwei Signalen | A6.9, A8.6 |
| 17b/18a/19a | Fahrplan nach Perioden-Auslastung; Sortierung bleibt; Bus/Tram/Metro/Zug/Fähre | A8.7 |
| 20a/21a | Spezifikation, Register, Claims, Pipeline-Art `line_health`, Export `-health`, Lean, Harness; direkt v2 | — |
| 24b→31a | Prefab-Standardintervalle als Takt hätten die 15 % unerreichbar gemacht (Kandidat 20,5 % → ≈ 3 %); stattdessen die Fahrzeugspanne des Spiels als Taktbedingung, keine Tabelle | A5.5 geschlossen, A6.8 |
| 26a | eigenes Urteil „Flotte unter Soll“ aus den Spielflags | A8.6 |
| 28a | nearest-rank-Quantil | A8.3 |
| 29a | Spielspanne auch bei Vorschlägen (Set-Zulässigkeit bei der Flotte, die die Set-Fahrgäste bemessen) | §6.3 F1 v3 |
| 30a | Fahrgäste je Bestandslinie aus dem Basislauf des Routings | A8.2 |
| Nutzerregel | alle Rechenkonstanten in `Common/Planning/Assumptions.cs`, kein duplizierter Code | CLAUDE.md |

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
