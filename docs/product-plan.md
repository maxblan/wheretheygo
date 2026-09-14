# Produktplan: Transit Architect als Nachfrage- und Lückenkarte

Stand 2026-09-14. Ergebnis der Richtungsentscheidung vom 2026-09-13/14 (Autor + Recherche über
die CS2-Modlandschaft und die Spielererwartungen). Dieses Dokument beschreibt das **Endresultat**
aus Sicht des Spielers: was die Mod ist, was sie zeigt, was sie ausdrücklich nicht tut und warum.
Es enthält keine Implementierungsschritte. Eine neue Session soll daraus den Umbau ableiten.

Vorrang: Dieses Dokument löst `docs/redesign-plan.md` (Umbau zur beweisbar optimalen Planung)
und den Abschnitt „Route suggestion" der `README.md` ab. Wo `docs/formal-specification.md` die
Stufen S4 bis S7 beschreibt, gilt sie nur noch als Beschreibung des Ist-Zustands, nicht als Ziel.

---

## 1. Identität

**Die Mod zeigt, wo die Menschen der Stadt hinwollen und was das Verkehrsnetz davon noch nicht
trägt. Bauen tut der Spieler.**

Das war die ursprüngliche Idee: Die Datenanzeige des Spiels reicht nicht aus, um zu sagen, wo
was gebraucht wird. Cities: Skylines II kennt jeden Wohnort, jeden Arbeitsplatz, jede Schule und
jedes beobachtbare Reiseziel, zeigt dem Spieler aber nur Auslastungsprozente je Linie. Die Mod
macht die Nachfrage sichtbar, die im Save steckt, und legt darüber, welcher Teil davon schon
mit dem ÖPNV fährt.

### Der Prüfstein

Jede Funktion muss eine dieser beiden Fragen beantworten, und keine andere:

1. **Wo wollen die Leute hin, und wer davon fährt heute Auto?**
2. **Was leistet eine Linie, die der Spieler gebaut hat, für diese Wege?**

Eine Funktion, die dem Spieler sagt, *was er bauen soll*, fällt durch. Eine Funktion, die für
ihn *am Spiel etwas verändert* (Fahrzeugzahl, Fahrplan, Preis), fällt durch. Eine Funktion,
deren Zahl der Spieler nicht auf eine Herkunft zurückführen kann, fällt durch.

### Warum so

- Spieler fordern seit dem Launch in fast jedem Thread genau Frage 1 („Wo wollen die Cims
  hin?", „Warum ist meine Linie leer?"). Niemand hat nach automatischen Linienvorschlägen
  gefragt; der einzige Auto-Router für CS1 kam auf rund 0,4 % der Abonnenten des führenden
  Insight-Mods, und seine Kommentare handeln von den Vorschlägen, die falsch lagen.
- Die Communities von Factorio, Anno, OpenTTD und Transport Fever ziehen dieselbe Grenze:
  Fleißarbeit automatisieren ja, die Entscheidung über den Linienverlauf ist das Spiel.
  OpenTTDs Cargodist ist das Vorbild: Nachfrage verteilen, Auslastung farbig zeigen, nie eine
  Route vorschlagen.
- Ein sichtbar falscher Vorschlag kostet mehr Vertrauen, als zehn gute einbringen. Eine
  Karte, die nur zeigt, was im Save steht, kann nicht falsch liegen.
- Die Mod hieß bis vor Kurzem „Station Suitability Overlay" und trägt seither vier Produkte in
  sich (Heatmap, Nachfrage, Linienvorschläge, Linienurteile). Der Schnitt auf Frage 1 und 2
  macht daraus eines.

---

## 2. Abgrenzung: was die Mod nicht tut

Regel: **Was eine vielbenutzte Mod (Größenordnung zehntausend Abonnenten und mehr, gepflegt)
bereits gut macht, macht diese Mod nicht.** Bei Überschneidung im Konzept muss die eigene
Funktion eine andere Frage beantworten, nicht dieselbe besser.

| Funktion | Gehört zu | Haltung dieser Mod |
|---|---|---|
| Fahrzeugzahl je Linie automatisch anpassen, Preis regeln | SmartTransportation (31 k), Transport Dynamic Scaling (6 k), TransitTimetables (12 k) | Nie. Kein Knopf verändert eine Linie. |
| Fahrpläne, Taktzeiten, Abfahrtstafeln, Disposition | TransitTimetables (12 k), Advanced Transit Operations (12 k) | Nie. |
| Ersatz oder Erweiterung der Verkehrsübersicht (Linienliste) | Better Transit View (139 k), Xtended Transport Manager (88 k) | Keine Spalte, kein Tab, keine Abhängigkeit von den Komponenten dieses Panels. Better Transit View ersetzt das Vanilla-Panel komplett; alles, was daran hängt, verschwindet bei 139 k Spielern. |
| Routen einzelner Cims, Fahrzeuge oder eines angeklickten Straßenstücks | Traffic Spy (32 k), Cim Route Highlighter (34 k), NavigationView (9 k), Vanilla „Traffic Routes" seit 1.2 | Nie. Die Mod zeigt aggregierte Nachfrage, nie ein Individuum. |
| Wartende je Haltestelle als Liste | Crowded Stops (5 k), Better Transit View | Nie. |
| Fahrzeugfahrten-Heatmap, Straßenverkehrsfluss | Traffic Watch (5 k), Traffic Tool Essentials (236 k) | Nie. Die Mod zeigt Wege von Menschen Tür zu Tür, nicht Fahrten von Fahrzeugen. |
| Stadtweite Statistiken je Modus, OD-Matrix als Tabelle | Trips View (2 k) | Keine Tabellen, keine Diagramme. Zwei Kennzahlen im Infoview-Panel, mehr nicht. |
| Pathfinder-Kosten, Gehgeschwindigkeit, Umsteigestrafe verändern | Realistic PathFinding (62 k) | Nie. Wo die Mod einen Weg berechnen muss, spiegelt sie das Vanilla-Modell und sagt das. |
| Wann und warum Cims reisen (Schichten, Pendelspitzen) | Realistic Trips (76 k) | Nie verändern. Die Mod beobachtet, was ist, und zeigt es mit Tageszeit. |
| Haltestellen-Kreise um Einzugsbereiche, Hotspots unbedienter Reiseziele | Transit Hotspots (10 k, eine Version März 2026, seither nicht gepflegt) | **Grauzone.** Die Lückenschicht (Abschnitt 3.3) beantwortet eine verwandte Frage mit Gehzeit statt Radius und mit Routing statt Nähe. Entscheidung des Autors, siehe Abschnitt 7. |
| Linien vorschlagen, Halte setzen, Modus wählen | Niemand | Nie mehr. Das war der Kern des alten Schnitts und wird entfernt. |

---

## 3. Das Endresultat aus Spielersicht

Die Mod ist ein **einziges Infoview** im Infoview-Menü des Spiels, in Vanilla-Optik, plus eine
**Sektion im Fenster einer angeklickten Linie**. Es gibt keinen Toolbar-Knopf, kein eigenes
Fenster und keinen Eingriff in die Verkehrsübersicht. Alle Einstellungen liegen in Options →
Transit Architect.

### 3.1 Schicht „Wunschlinien"

Die Hauptansicht. Jede Reise, die die Mod kennt (Wohnung → Arbeit, Wohnung → Schule aus dem Save;
Einkauf und Freizeit aus der laufenden Beobachtung), wird als Band zwischen ihren beiden Enden
gezeichnet, gebündelt über die Stadt.

- **Breite** = Zahl der Wege. Logarithmische Skala, damit der eine Riesenkorridor die anderen
  nicht verschluckt.
- **Farbe** = Anteil der Wege, die das bestehende Netz bereits in vertretbarer Zeit trägt. Warm
  heißt „hier fahren alle Auto", kühl heißt „die sitzen schon in deiner Metro". Das ist die
  Aussage der Mod auf einen Blick.
- **Bündelung.** Paare mit nahezu gleichem Verlauf verschmelzen zu einem Band. Ein Schwellwert,
  den der Spieler im Panel schiebt, blendet dünne Bänder aus. Die Stadt darf nie zu Spaghetti
  werden.
- **Bogen statt Gerade**, flach auf dem Gelände, damit kreuzende und gegenläufige Bänder
  auseinanderliegen. Über einen Fluss ohne Brücke geht das Band trotzdem: Es zeigt, wo eine
  Verbindung fehlt, nicht wo eine Straße ist.
- **Tageszeit.** Ein Schieber mit Abspielen über den Spieltag. Die Bänder atmen: morgens
  stadteinwärts, abends zurück. Die Richtung der Bewegung folgt der Tageszeit; ein starrer Pfeil
  „Wohnen → Arbeit" wäre die halbe Wahrheit, weil jeder Weg zweimal gefahren wird. Die Animation
  ist langsam und dezent.
- **Zweckfilter.** Arbeit, Schule, Einkauf, Freizeit einzeln schaltbar.
- **Zeigen und Anklicken.** Ein Band nennt beim Zeigen seine Zahlen: Wege gesamt, Anteil ohne
  ÖPNV, Spitzenstunde. Das braucht ein eigenes Werkzeug, weil der Overlay-Puffer nur Geometrie
  zeichnet; es ist Teil des Endresultats, nicht optional.

Die Wunschlinie ist die reine Nachfrage, ohne Straßenzuordnung. Eine zweite Darstellung, der auf
die Straßen verteilte Fluss (was heute die Schicht „Travel demand" zeigt), bleibt als
Unteransicht erhalten, weil sie zeigt, wo ein Bus fahren müsste, um dieselbe Nachfrage
einzusammeln. Die reine Form ist die Voreinstellung.

### 3.2 Schicht „Erschließung"

Jedes Gebäude wird nach der Gehzeit zur nächsten **bedienten** Haltestelle eingefärbt (existiert
heute, bleibt). Bedient heißt: eine Linie hält dort. Eine Haltestelle ohne Linie zählt nicht. Die
Gehzeit ist echte Gehzeit über das Fußwegenetz, nicht Luftlinie. Das Gebäudefenster zeigt den
Wert, der die Farbe bestimmt hat.

### 3.3 Schicht „Lücken" (Entscheidung offen, siehe 7)

Eine einzige Geländeschicht mit einer einzigen Bedeutung: **Wie viele unbediente Wege haben
eine Tür in Gehzeit dieser Stelle.** Keine sieben gewichteten Terme, keine Perzentil-Normierung,
keine Kalibrierung, keine Modusgewichte. Der Spieler liest sie so: „Eine Haltestelle hier wäre
für so viele Autofahrer erreichbar." Ob er dort einen Bus oder eine Metro hinstellt, entscheidet
er anhand der Wunschlinien.

### 3.4 Zwei Kennzahlen im Panel

- Anteil aller Wege, die das Netz heute trägt.
- Anteil der Einwohner, die Wohnung **und** Ziel in Gehzeit einer bedienten Haltestelle haben.

Beide mit Tooltip, der sagt, woraus sie gerechnet sind. Keine weiteren Zahlen, keine Diagramme.

### 3.5 Linienprüfung: eine Sektion im Fenster der angeklickten Linie

Frage 2 des Prüfsteins. Der Spieler baut eine Linie (auch eine frische, die noch keine Fahrzeuge
hat) und klickt sie an. Die Mod zeigt als Lesung, nicht als Urteil:

- **Wege, deren schnellster Weg diese Linie nutzt**, und die Minuten, die sie ihnen gegenüber
  Gehen und dem restlichen Netz spart. Ein kurzer Zubringer, dessen Korridor selbst leer ist,
  wird hier sichtbar wertvoll, weil er Wege auf eine Stammlinie bringt.
- **Anteil dieser Wege, der ohne die Linie nicht langsamer wäre.** Das ist die Antwort auf
  „Warum ist meine Linie leer?": Sie fährt parallel zu etwas, das es schon gibt.
- **Last je Tagesstunde** gegen die Kapazität der eingesetzten Fahrzeuge, gelesen aus dem
  eigenen Verlauf der Linie (existiert heute als Fensterung über einen Spieltag). Ob daraus ein
  Fahrzeug mehr oder ein Nachtbetrieb folgt, sieht der Spieler selbst; die Mod sagt es nicht
  und stellt es nicht ein.
- Beim Anklicken der Linie leuchten in der Wunschlinien-Schicht die Bänder auf, die sie trägt.

Keine Verdikte („Flotte zu klein", „Modus zu klein", „Linie entfernen"), keine
Benachrichtigungen, keine Ein-Klick-Aktionen. Das Wort „Empfehlung" kommt in der Mod nicht vor.

### 3.6 Grundsätze der Darstellung

- Vanilla-Optik: das Infoview sieht aus wie Grundwasser oder Bodenwert, die Legende zeichnet das
  Spiel. Farbrampen farbenblind-sicher, kühl gegen warm, beide in der Helligkeit monoton.
- Kein Element bewegt sich schnell. Die Bänder sind die Bühne, nicht das Feuerwerk.
- Nichts der Mod ist aktiv, solange der Spieler das Infoview nicht geöffnet hat, außer der
  stillen Beobachtung der Reisen, die die Tageszeit- und Einkaufsdaten liefert.
- Englisch und Deutsch vollständig.

---

## 4. Grundsätze der Berechnung

- **Daten vor Modell.** Wo das Save die Antwort enthält (Wohnort, Arbeitsplatz, Schule,
  beobachtetes Ziel, Haltestellen, Linien, Fahrzeuge), wird gelesen, nicht geschätzt. Es gibt
  kein Gravitationsmodell und keine Kalibrierung.
- **Wo ein Modell nötig ist, wird Vanilla gespiegelt und offengelegt.** Die Frage „trägt das
  Netz diesen Weg" braucht ein Routing über die bestehenden Linien. Es rechnet mit den Regeln
  des Spiel-Pathfinders (Gehen, Warten, Fahren gleich gewichtet, Umstieg kostet Gehweg und
  Wartezeit). Die Mod sagt im Tooltip, dass sie so rechnet, und dass Mods wie Realistic
  PathFinding andere Regeln setzen; sie versucht nicht, diese nachzubilden.
- **Jede Zahl hat eine Herkunft**, die der Spieler mit einem Blick in den Tooltip nachlesen
  kann: „1 240 Wege, davon 840 ohne ÖPNV; Wohnungen im Norden, Arbeitsplätze im Hafen."
- **Zeit ist Gehzeit über das Fußwegenetz**, nie Luftlinie, außer die Darstellung sagt es
  ausdrücklich (die Wunschlinie selbst ist Luftlinie, das ist ihr Sinn).
- **Jede Konstante steht in `Assumptions.cs`**, unverändert die Regel vom 2026-09-06.
- **Der rechnende Kern bleibt rein** (kein Unity-Typ in `Planning/`), damit er offline getestet
  wird. Das gilt für Bündelung, Routing, Gehzeit und die Zeitfenster.
- **Budgets bleiben bei den ECS-Systemen**: Worker-Thread, Debounce, Grenzen für Bänder und
  Beobachtungsfenster, Logmeldung beim Abschneiden.

---

## 5. Was wegfällt und warum

| Wegfall | Grund |
|---|---|
| Linienvorschläge komplett: Korridorsuche, Gitter für Schiene und Wasser, Halteplanung, Moduswahl, Set-Optimierung, Umsteigepunkt-Anpeilung, Staging und „Anwenden"-Knopf, gespeicherte Vorschläge | Beantwortet „was bauen", nicht „was fehlt". Niemand fragt danach, niemand sonst baut es, ein falscher Vorschlag kostet Vertrauen. Größter Komplexitätsposten der Mod. |
| Station-Suitability-Heatmap mit sieben Termen, Modusgewichten, Perzentilen, Ranglisten-Standorten | Für den Spieler nicht lesbar; keine Zahl lässt sich auf eine Herkunft zurückführen. Ersetzt durch die Lückenschicht mit einer Bedeutung (falls behalten) und die Wunschlinien. |
| Ridership-Kalibrierung (Regression, R², „Apply fitted weights") | Existierte nur für die Gewichte der Heatmap. Liegt schon hinter dem Entwicklerschalter. |
| Linienurteile (ModeUp, ModeDown, Remove, Fleet, Schedule), Benachrichtigungen, Ein-Klick-Aktionen auf Flotte und Fahrplan | Flottenautomatik ist besetzt (SmartTransportation u. a.); Urteile sind Automation im Gewand der Beratung. Ersetzt durch die Lesung in 3.5. |
| Spalte je Linie und Vorschlags-Tab in der Verkehrsübersicht | Kollidiert mit Better Transit View (139 k). Die Mod hängt nur an Vanilla-Flächen, die keine große Mod ersetzt: Infoview-Menü, Infoview-Panel, Fenster des angeklickten Objekts. |
| Route-Objective-Einstellung (Ridership / Coverage / Balanced), Modus-Voreinstellungen mit Gewichten und Radien | Ohne Vorschläge und Heatmap gegenstandslos. Die Optionsseite schrumpft auf: Gehzeit-Horizont für „erschlossen", Bänder-Schwellwert-Voreinstellung, Beobachtungsfenster. |
| Verifikationspipeline (`verification/`, SCIP, VIPR, Lean-Beweise, Export-Knopf, `ExportJson`) | Diente dem Nachweis, dass der Optimierer korrekt ist. Ohne Optimierer bleibt der Offline-Test des reinen Kerns als Nachweis. |
| Dokumente `redesign-plan.md`, `scientific-model-review.md`, `correctness-claims.md`, `verification-architecture.md`, `assumptions-register.md` in ihrer heutigen Form | Beschreiben den alten Schnitt. Was an Fakten über das Spiel darin steht (Fahrzeugkapazitäten, Pathfinder-Kosten, Halteverzögerung aus Prefabs), wandert in ein kurzes Faktenblatt. |

## 6. Was bleibt und weiterverwendet wird

- Das Lesen der Wege aus dem Save (Wohnung, Arbeit, Schule) und die Beobachtung von Einkauf und
  Freizeit mit Tageszeit, samt Ablage im Save, damit eine geladene Stadt nicht kalt startet.
- Die Zonenaggregation und die Unterscheidung bedient / unbedient durch Routing über das
  bestehende Netz.
- Der Transitgraph aus den bestehenden Linien (Halte, Fahrten, Umsteige-Fußwege) und das
  Tür-zu-Tür-Routing. Er trägt heute die Set-Optimierung; künftig trägt er die Farbe der Bänder
  und die Linienprüfung.
- Die Gehzeit-Dijkstra über das Fußwegenetz und die Gebäudefärbung.
- Der Linienverlauf über einen Spieltag (Fahrgäste an Bord, Kapazität, Fahrzeuge draußen) als
  Datenquelle für „Last je Tagesstunde".
- Die Infoview-Mechanik in Vanilla-Optik, die Legende aus dem Prefab, die Farbrampen.
- Die dekompilierten Spielfakten (Flottenarithmetik, Buffer-Ordnung, Infoview-Gültigkeit,
  Umsteige-Hub als Menge von Haltestellen), auch wenn ein Teil davon nur noch für die
  Linienprüfung gebraucht wird.
- Die Regeln des Repos: Reinheitsregel, eine Konstantendatei, Tests ohne Framework, Logs als
  Instrument, Dekompilieren vor Benutzen.

---

## 7. Offene Entscheidungen des Autors

1. **Lückenschicht behalten oder nicht?** Sie ist die einzige Grauzone gegenüber Transit
   Hotspots (10 k, wahrscheinlich verwaist). Für: Sie beantwortet „wo hin mit der Haltestelle"
   mit einer einzigen lesbaren Zahl. Gegen: Die Enden der warmen Bänder zeigen fast dasselbe,
   und eine Schicht weniger ist eine klarere Identität.
2. **Name der Mod.** „Transit Architect" verspricht, dass jemand für den Spieler entwirft. Der
   neue Schnitt verspricht das Gegenteil. Eine Umbenennung vor der ersten Veröffentlichung ist
   billig, danach nicht mehr. Das Infoview heißt jedenfalls nicht mehr „Station Suitability".
3. **Straßenzugeordneter Fluss als Unteransicht** behalten oder nur die reine Wunschlinie zeigen?
4. **Beobachtungsfenster** für Einkauf und Freizeit: ein Spieltag (heute) oder länger, damit die
   Tageszeit-Animation ruhiger wird?

## 8. Woran das Ergebnis gemessen wird

- Ein Spieler, der die Mod nie gesehen hat, öffnet das Infoview und kann nach einer Minute ohne
  Anleitung sagen: „Zwischen hier und dort fehlt eine Verbindung."
- Er kann eine Linie zeichnen, sie anklicken und sehen, welche Bänder sie trägt und wie viele
  Minuten sie spart. Ändert er die Linie, ändert sich die Lesung.
- Keine Zahl in der Mod, die er nicht per Tooltip auf ihre Herkunft zurückführen kann.
- Kein Knopf in der Mod, der etwas an seiner Stadt verändert.
- Die Mod läuft neben Better Transit View, SmartTransportation, Realistic PathFinding,
  Realistic Trips und Traffic Tool Essentials, ohne dass eine Funktion verschwindet oder doppelt
  erscheint.
- Bei einer Stadt mit 200 000 Einwohnern bleibt das Spiel flüssig, während das Infoview offen
  ist und der Tageszeit-Schieber läuft.

## 9. Was der neuen Session mitzugeben ist

Lesen, in dieser Reihenfolge: dieses Dokument, `CLAUDE.md`, `README.md` (Abschnitte „Travel
demand" und „Line health" beschreiben die Datenquellen, die bleiben), `docs/ui-architecture.md`
(die Vanilla-Flächen, an die die Mod andocken darf), `.claude/rules/*.md`. Die Recherche zur
Modlandschaft und zu den Spielererwartungen liegt im Gedächtnis der Vorsession unter
„Mod identity rethink 2026-09-13"; ihre Kernaussagen stehen in Abschnitt 1 und 2.

Die erste Aufgabe der neuen Session ist nicht Code, sondern ein Umbauplan aus diesem Dokument,
mit der Reihenfolge: zuerst Bündelung, Schwellwert und die Farbe bedient / unbedient, weil sie
die Lesbarkeit entscheiden; dann Tageszeit mit Richtung; dann die Linienprüfung; zuletzt das
Anklicken der Bänder, weil es ein eigenes Werkzeug braucht. Abrisse (Abschnitt 5) laufen davon
getrennt, jeder als eigener Schritt, jeder mit grünem `make strict`.
