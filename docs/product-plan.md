# Produktplan: Where They Go, die Nachfragekarte

Stand 2026-09-14, Umsetzung eingetragen am 2026-09-20. Ergebnis der Richtungsentscheidung
vom 2026-09-13/14 (Autor + Recherche über die CS2-Modlandschaft und die Spielererwartungen).
Dieses Dokument beschreibt das **Endresultat** aus Sicht des Spielers: was die Mod ist, was
sie zeigt, was sie ausdrücklich nicht tut und warum.
Es enthält keine Implementierungsschritte.

Die Dokumente, die dieses hier abgelöst hat (`redesign-plan.md`, `formal-specification.md`,
`scientific-model-review.md`, `correctness-claims.md`, `verification-architecture.md`,
`assumptions-register.md`), sind entfernt; was an Spielfakten in ihnen stand, steht in
`docs/game-facts.md`.

---

## 0. Stand der Umsetzung

**2026-09-20: Der Umbau ist gebaut.** Ab hier beschreiben die Abschnitte 3 bis 6 den
Ist-Zustand, nicht mehr ein Ziel. Wo der Bau anders ausgefallen ist als der Plan vom 14.9.,
steht die geänderte Entscheidung an Ort und Stelle, ausgezeichnet als **Geändert** oder
**Verworfen**. Die vier offenen Fragen aus Abschnitt 7 sind beantwortet. Nichts in diesem
Dokument ist noch eine Aufgabe.

Was anders kam, auf einen Blick:

- Ein **Knopf oben links** statt des Eintrags im Infoansicht-Menü (3).
- Die **Lückenschicht** wird nicht gebaut (3.3, Frage 7.1).
- Der **straßenzugeordnete Fluss** entfällt mit der alten Schicht „Travel demand" (3.1,
  Frage 7.3).
- Die zweite Kennzahl zählt **Wege**, nicht Einwohner (3.4).
- Die Mod **rechnet auch bei geschlossenem Infoview** (3.6).
- Der Pathfinder-Hinweis steht in der README statt im Tooltip (4).
- Die Optionsseite bekommt **weder Schwellwert noch Beobachtungsfenster** (5).

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

Die Mod ist ein **einziges Infoview** in Vanilla-Optik, plus je eine **Sektion im Fenster
einer angeklickten Linie** und eines angeklickten Gebäudes. Es gibt kein eigenes Fenster und
keinen Eingriff in die Verkehrsübersicht. Alle Einstellungen liegen in Options → Where They Go.

**Geändert 2026-09-20:** Der Weg ins Infoview ist ein **Knopf oben links**, neben denen der
anderen Mods; der Eintrag im Infoansicht-Menü wird dafür ausgeblendet, damit nicht zwei Türen
in denselben Raum führen. Der Plan vom 14.9. schloss einen Toolbar-Knopf aus, um keine Fläche
zu belegen, die eine große Mod ersetzt. Der Knopf belegt keine: er hängt am Modding-Haken
„GameTopLeft", den das Spiel selbst dafür vorsieht.

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

Die Wunschlinie ist die reine Nachfrage, ohne Straßenzuordnung.

**Verworfen 2026-09-20 (Frage 7.3):** Der auf die Straßen verteilte Fluss bleibt nicht als
Unteransicht erhalten. Die alte Schicht „Travel demand" ist mit dem Abriss aus Abschnitt 5
gefallen, und eine zweite Schicht, die fast dasselbe sagt, kostet mehr Lesbarkeit als sie
einbringt. Eine Schicht, eine Bedeutung.

### 3.2 Schicht „Erschließung"

Jedes Gebäude wird nach der Gehzeit zur nächsten **bedienten** Haltestelle eingefärbt (existiert
heute, bleibt). Bedient heißt: eine Linie hält dort. Eine Haltestelle ohne Linie zählt nicht. Die
Gehzeit ist echte Gehzeit über das Fußwegenetz, nicht Luftlinie. Das Gebäudefenster zeigt den
Wert, der die Farbe bestimmt hat.

### 3.3 Schicht „Lücken" — verworfen

**Verworfen 2026-09-20 (Frage 7.1).** Die Schicht wird nicht gebaut. Die warmen Enden der
Bänder und die Gebäudefärbung beantworten „wo hin mit der Haltestelle" zusammen schon; damit
ist auch die einzige Grauzone gegenüber Transit Hotspots ausgeräumt. Eine Schicht weniger ist
die klarere Identität.

### 3.4 Zwei Kennzahlen im Panel, der Rest zugeklappt

Sichtbar, sobald das Infoview offen ist:

- Anteil aller Wege, die das Netz heute trägt.
- Anteil der **Wege**, deren beide Enden in Gehzeit einer bedienten Haltestelle liegen.
  (**Geändert 2026-09-20:** gezählt wird Weggewicht, nicht Einwohner. Die Kennzahl steht
  neben der ersten, die Wege zählt, und zwei Bezugsgrößen nebeneinander liest niemand.)

Darunter ein Bereich „Genauer", **zugeklappt als Voreinstellung** (Entscheidung des Autors
2026-09-20). Er trägt, was die beiden Kennzahlen verschweigen: den Fußweg zur nächsten
bedienten Haltestelle in vier Klassen, und die Bezugsgrößen, an denen jede andere Zahl im
Panel hängt — Linien, bediente Haltestellen, Wege am Tag.

Die frühere Fassung dieses Abschnitts sagte „keine weiteren Zahlen, keine Diagramme".
Der Stundenstreifen und der Zweck-Balken waren da schon gebaut, sind Diagramme und
beantworten Frage 1 besser als jede Zahl. Und die Sparsamkeit hat geschadet: „Arbeit
35 606" allein bleibt unlesbar, wie viele Zahlen daneben stehen. Es gelten also drei
Regeln statt einer Obergrenze:

- **Ruhig als Voreinstellung.** Was ein Spieler beim Öffnen sieht, ist die Karte und zwei
  Kennzahlen. Alles andere ist eine Geste entfernt.
- **Keine Zahl ohne Bezugsgröße.** Jede absolute Zahl steht neben ihrem Anteil, ihrem Rang
  oder dem Ganzen, von dem sie ein Teil ist.
- **Der Prüfstein aus Abschnitt 1 gilt unverändert.** Keine dieser Zahlen sagt, was zu bauen
  ist, und jede lässt sich auf ihre Herkunft zurückführen.

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
- **Geändert 2026-09-20:** Die Mod rechnet auch bei geschlossenem Infoview weiter — die
  Beobachtung der Reisen, die Erschließung, das Routing und die Linienlesung. Die Sektionen
  im Gebäude- und im Linienfenster sind ohne Infoview sichtbar und wären sonst leer, und ein
  Beobachtungsfenster, das beim Zuklappen stehenbleibt, liefert hinterher falsche Zahlen. Am
  Infoview hängt das Zeichnen.
- Englisch, Deutsch, Französisch, brasilianisches Portugiesisch, Russisch und vereinfachtes
  Chinesisch vollständig; jede fehlende Zeile fällt auf Englisch zurück.

---

## 4. Grundsätze der Berechnung

- **Daten vor Modell.** Wo das Save die Antwort enthält (Wohnort, Arbeitsplatz, Schule,
  beobachtetes Ziel, Haltestellen, Linien, Fahrzeuge), wird gelesen, nicht geschätzt. Es gibt
  kein Gravitationsmodell und keine Kalibrierung.
- **Wo ein Modell nötig ist, wird Vanilla gespiegelt und offengelegt.** Die Frage „trägt das
  Netz diesen Weg" braucht ein Routing über die bestehenden Linien. Es rechnet mit den Regeln
  des Spiel-Pathfinders (Gehen, Warten, Fahren gleich gewichtet, Umstieg kostet Gehweg und
  Wartezeit). **Geändert 2026-09-20:** Dieser Hinweis steht in der README, nicht im
  Tooltip. Im Panel sagt jede Zahl, woraus sie gemacht ist; den Pathfinder nennt keine.
  Nachgebildet wird Realistic PathFinding weiterhin nicht.
- **Jede Zahl hat eine Herkunft**, die der Spieler mit einem Blick nachlesen kann. Der
  Tooltip eines Bandes nennt Wege, Anteil ohne ÖPNV, Spitzenstunde, Abstand der Enden und
  die Zwecke; jede Zahl im Panel trägt eine Bildunterschrift, woraus sie gemacht ist.
  (**Geändert 2026-09-20:** die Enden werden mit Zahlen beschrieben, nicht mit Ortsnamen —
  „Wohnungen im Norden, Arbeitsplätze im Hafen" hätte eine Benennung von Stadtteilen
  gebraucht, die das Spiel nicht hergibt.)
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
| Route-Objective-Einstellung (Ridership / Coverage / Balanced), Modus-Voreinstellungen mit Gewichten und Radien | Ohne Vorschläge und Heatmap gegenstandslos. Die Optionsseite schrumpft auf zwei Schalter: den Gehzeit-Horizont für „erschlossen" und das Infoview selbst. **Geändert 2026-09-20:** Schwellwert und Beobachtungsfenster bleiben Konstanten in `Assumptions.cs` — der Schwellwert steht als Schieber im Panel, wo er wirkt, und das Beobachtungsfenster ist eine Messgröße, keine Vorliebe. |
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

## 7. Die offenen Entscheidungen, beantwortet

Alle vier am 2026-09-20 entschieden.

1. **Lückenschicht: nein.** Sie wird nicht gebaut. Siehe 3.3.
2. **Name: „Where They Go".** Umbenannt vor der ersten Veröffentlichung, wie es billig war;
   `TransitArchitect` und der `Suitability`-Präfix kommen nirgends mehr vor.
3. **Straßenzugeordneter Fluss: nein.** Nur die reine Wunschlinie. Siehe 3.1.
4. **Beobachtungsfenster: drei Spieltage** statt einem. Ein einzelner Tag lässt das Bild
   Stunde für Stunde springen, weil eine Stunde nur ein paar hundert Beobachtungen bringt;
   drei Tage beruhigen es bei dreifachem Speicher. Jede beobachtete Reise wiegt dafür ein
   Drittel, damit sie neben den aus dem Save gelesenen Wegen nicht dreifach zählt.

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

## 9. Was einer neuen Session mitzugeben ist

Lesen, in dieser Reihenfolge: dieses Dokument, `CLAUDE.md`, `README.md` (die beschreibt das
Ergebnis für einen Spieler), `docs/game-facts.md` (die dekompilierten Spielfakten, auf die der
Code sich stützt), `.claude/rules/*.md`. Die Recherche zur Modlandschaft und zu den
Spielererwartungen liegt im Gedächtnis der Session vom 2026-09-13; ihre Kernaussagen stehen in
Abschnitt 1 und 2.

Der Umbau, den dieses Dokument beschrieben hat, ist abgeschlossen. Es ist ab hier die
Beschreibung dessen, was die Mod ist, und der Prüfstein aus Abschnitt 1 ist das, woran ein
Vorschlag für eine neue Funktion gemessen wird.
