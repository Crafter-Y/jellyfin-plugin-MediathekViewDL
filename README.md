# 📺 Jellyfin MediathekViewDL Plugin

## Automatic streaming catalogue (Crafter-Y fork, 0.8.4.1)

This fork adds an opt-in catalogue for normal Jellyfin libraries without downloading programme videos or maintaining individual show subscriptions.

Bulk catalogue queries use exact broadcaster names, so `SR` does not inadvertently fetch `SRF`. Virtual playback uses the small upstream `/api/entries` lookup after a cache miss instead of traversing the whole subscription again (one measured response was 1,161 bytes). Catalogue file changes use a recoverable append-only journal, compacted into the full manifest at checkpoints, to avoid rewriting a large manifest for every new episode. Movie evidence must be an explicit production/type label in the description, not merely a discussion mentioning a feature film.

1. Open **Einstellungen → Streaming-Katalog (automatisch)**. Enable the catalogue, choose the broadcasters and set an **empty, dedicated absolute directory** on the server, for example `/media/Mediathek`.
2. Optionally include series. Only episodes with reliable season **and** episode numbers are accepted; they are grouped by the actual programme topic, never by a broadcaster-wide subscription name.
3. Save and run **Katalog jetzt synchronisieren**. Add `/media/Mediathek/Movies` as a Jellyfin Movies library and `/media/Mediathek/Shows` as a Shows library. Keep normal movie/TV metadata and artwork providers enabled, but disable trickplay generation and video thumbnail extraction for these streaming libraries.

The scheduled task runs every six hours. It performs a complete broadcaster traversal initially and at least daily; intermediate runs overlap seven days of broadcast dates. Page size and page guard are independent of interactive search limits. Oversized queries are divided into disjoint duration ranges to stay within the upstream 10,000-result search window; the API uses exclusive duration bounds. A pathological range with over 10,000 records of the same integer duration fails safely rather than silently truncating. Changing snapshots, failed pages and page-guard exhaustion fail the task without committing its checkpoint or retiring links. Broad virtual subscriptions use the same partitioned traversal, with playback re-resolution after cache eviction or restart. Bulk page requests are serialized and paced to at most one new request per second across catalogue and virtual subscriptions; transient-error retries already use exponential backoff. A measured 500-record page was about 607 KB uncompressed: 100,000 records would be roughly 120 MB per full traversal, before partition discovery and retries. Overlap runs fetch less, but a full-catalogue setup is heavier than normal interactive browsing.

Movie selection is deliberately conservative: a long video with no episode number is **not** automatically a film. Positive fiction evidence is required; trailers, factual programmes, documentaries and ambiguous records are skipped. This is a useful subset of MediathekView's indexed content, not a complete mirror of every broadcaster website. Unnumbered series and perfect classification are future work. Production years are extracted only when provided; broadcast years are never substituted. NFO files preserve broadcaster descriptions and known external IDs. Jellyfin's configured providers supply posters and external metadata where a match is available; perfect IMDb/TMDB identification is not guaranteed.

Only `.strm` links and `.nfo` metadata are written, with stable identities and broadcaster/quality duplicate selection. No programme video, subtitle or trickplay files are downloaded by the catalogue. Links are direct broadcaster URLs: playback remains subject to regional restrictions, availability and client compatibility. Live TV remains independently provided by the existing Zapp integration.

The dedicated root has an ownership marker; a private manifest lives in the plugin data directory under `catalogue`. Existing media directories, symlinks and user-edited files are protected. An item disappearing from the feed is not enough to remove it. After a successful full traversal, an absent stream must return 404/410 on at least three independent checks, over at least the configured grace period (default 72 hours). Authentication failures, rate limits, server errors, timeouts and network outages retain the item. Expired, unmodified links and their NFO files are moved to the plugin's private `catalogue/retired` directory for recovery. Videos and unrelated files are never retired. Empty library folders are left alone. To restore retired links, copy the relevant files back to their original library location; rediscovered items are automatically recreated. Do not change the root with an existing manifest: migrate the root and manifest explicitly while the task is disabled.

For a manual installation, build with .NET 10 and Node.js, using `dotnet publish Jellyfin.Plugin.MediathekViewDL -c Release -p:Version=0.8.4.1 -p:AssemblyVersion=0.8.4.1 -p:FileVersion=0.8.4.1`. Back up the installed plugin and configuration, **stop Jellyfin**, then replace the three DLLs listed in `build.yaml`, preserve the plugin GUID, set the local manifest to the fork version and disable upstream automatic updates for that installation. Start Jellyfin again. Never overwrite a loaded DLL in place: on Linux, this can corrupt the running process's mapped assembly. The upstream repository listed below installs the upstream plugin, not this catalogue patch.

**Ein leistungsstarkes Jellyfin-Plugin zum Suchen und Herunterladen von Inhalten aus den öffentlich-rechtlichen Mediatheken (ARD, ZDF, etc.).**

Dieses Plugin integriert die [MediathekViewWeb-API](https://mediathekviewweb.de/) direkt in Jellyfin. Es ermöglicht das automatische Abonnieren von Sendungen, das intelligente Verwalten von Downloads und die nahtlose Integration in Ihre Medienbibliothek.

---

## 📑 Inhalt

*   [✨ Features](#-features)
*   [🚀 Installation](#-installation)
*   [🧙 Einrichtungs-Assistent](#-einrichtungs-assistent)
*   [⚙️ Konfiguration & Nutzung](#-konfiguration--nutzung)
    *   [Manuelle Suche](#-manuelle-suche)
    *   [Allgemeine Einstellungen](#-allgemeine-einstellungen)
    *   [Abonnements (Automatische Downloads)](#-abonnements-automatische-downloads)
    *   [Downloads](#-downloads)
    *   [Logs](#-logs)
*   [🧠 Funktionsweise](#-funktionsweise)
*   [❤️ Danksagung & Disclaimer](#-danksagung--disclaimer)

---

## ✨ Features

| Bereich | Funktionen |
| :--- | :--- |
| **🔎 Suche & Abos** | • **Automatische Downloads:** Neue Episoden Ihrer Lieblingssendungen werden sofort geladen.<br>• **Manuelle Suche:** Durchsuchen Sie die gesamte Mediathek direkt in Jellyfin.<br>• **Smart Filters:** Filtern nach Thema, Sender oder Titel. |
| **💾 Download-Manager** | • **Zentrale Übersicht:** Neuer Tab für aktive Downloads, Historie und Warteschlange.<br>• **Kontrolle:** Downloads pausieren, abbrechen oder priorisieren.<br>• **Duplikat-Schutz:** Eine lokale Datenbank verhindert doppelte Downloads. |
| **📂 Organisation** | • **Metadaten (NFO):** Generiert NFO-Dateien für perfekte Integration in Jellyfin/Kodi.<br>• **Streaming (.strm):** Optional nur verknüpfen statt herunterladen (spart Speicher).<br>• **Extras:** Separate Steuerung für Trailer, Interviews und Bonusmaterial.<br>• **MediathekViewDL-Metadaten:** Betten einen JSON-Block mit ID, Download-URL, Video-URLs, Untertitel-URL, Originaltitel, Original-Topic, Staffel-/Episodennummer (sowie absoluter Episodennummer) und Beschreibung in `.mkv`-Dateien (Matroska via ffmpeg) bzw. als Kommentar in `.strm`-Dateien ein. |

---

## 🚀 Installation

### 📦 Via Plugin Repository (Empfohlen)

Die einfachste Methode. Updates erfolgen automatisch über Jellyfin.

1.  Öffnen Sie in Jellyfin: **Dashboard** -> **Plugins** -> **Repositories**.
2.  Klicken Sie auf **(+) Repository hinzufügen**.
3.  Tragen Sie folgenden Namen und URL ein:
    *   **Name:** `MediathekViewDL` (oder beliebig)
    *   **Repository-URL:**
        ```url
        https://raw.githubusercontent.com/CatNoir2006/jellyfin-plugin-manifest/main/manifest.json
        ```
4.  Installieren Sie das Plugin nun über den **Katalog** und starten Sie Jellyfin neu.

### 🛠️ Manuell (Für Entwickler)
<details>
<summary><strong>🔽 Details</strong></summary>
<br>

1.  **Repository klonen:**
    ```bash
    git clone https://github.com/CatNoir2006/jellyfin-plugin-MediathekViewDL.git
    cd jellyfin-plugin-MediathekViewDL
    ```
2.  **Bauen:**
    ```bash
    dotnet build
    ```
3.  **Installieren:**
    *   Erstellen Sie einen Ordner `MediathekViewDL` im `plugins`-Ordner Ihrer Jellyfin-Installation.
    *   Kopieren Sie **alle Dateien** aus `bin/Debug/net9.0` (oder `Release`) in diesen Ordner.
4.  **Neustart:** Starten Sie den Jellyfin-Server neu.
</details>

---

## 🧙 Einrichtungs-Assistent

Bei einer **Neuinstallation** öffnet sich nach dem ersten Aufruf der Konfigurationsseite automatisch ein Einrichtungs-Assistent, der Sie in sieben kurzen Schritten durch die wichtigsten Einstellungen führt:

1. **Willkommen** – Kurze Einführung in das Plugin.
2. **Standard-Pfade** – Legen Sie fest, wo Serien, Filme und ggf. temporäre Downloads gespeichert werden.
3. **Speicher sparen** – Wählen Sie, ob neue Abos standardmäßig als `.strm`-Streamlinks statt als kompletter Videodatei gespeichert werden sollen (spart Speicher, benötigt aber Internet beim Abspielen).
4. **Live-TV (optional)** – Fügt den Zapp Tuner und/oder den Zapp Guide-Provider zur Jellyfin-Live-TV-Verwaltung hinzu.
5. **Erstes Abo (optional)** – Legt direkt ein erstes Abonnement mit Sender und Suchbegriff an.
6. **Tab-Tour** – Erklärt die fünf Tabs der Konfigurationsseite (Suche, Einstellungen, Abos, Downloads, Logs).
7. **Fertig** – Speichert den Status und schließt den Assistenten.

Den Assistenten können Sie jederzeit über die Schaltfläche **🧙 Einrichtungs-Assistent** im Kopfbereich der Konfigurationsseite erneut starten – auch nachdem Sie ihn bereits abgeschlossen oder übersprungen haben. Der Status wird in der Plugin-Konfiguration (`WizardCompleted`) gespeichert.

---

## ⚙️ Konfiguration & Nutzung

Das Plugin fügt einen neuen Menüpunkt im Hauptmenü sowie eine Konfigurationsseite im Dashboard hinzu.

### 🔍 Manuelle Suche
Hier können Sie gezielt nach Sendungen suchen, Downloads sofort starten oder Suchfilter direkt in ein Abo umwandeln.

<img src="Images/ManuelleSuche.png" width="800" alt="Manuelle Suche">

*   **Direkt-Download:** Startet den Download sofort.
*   **In Abo übernehmen:** Erstellt aus der aktuellen Suche ein dauerhaftes Abonnement.
*   **Ausschluss-Filter (NICHT):** Durch Voranstellen eines Ausrufezeichens (z. B. `!Wetter`) können Begriffe von der Suche ausgeschlossen werden.

<details>
<summary><strong>🔽 Erweiterter Download (Optionen)</strong></summary>
<br>
Über "Erweiterter Download" können Pfad, Dateiname und Untertitel-Optionen individuell angepasst werden.

<img src="Images/ManuellerDownloadErweitert.png" width="600" alt="Erweiterter Download">
</details>

### 🛠 Allgemeine Einstellungen
(Tab: *Einstellungen*)

Hier konfigurieren Sie das globale Verhalten des Plugins. Die Einstellungen sind in logische Gruppen (Pfade, Download, Suche, Netzwerk, Abo-Standardwerte, Wartung) unterteilt.
<details>
<summary><strong>🔽 Allgemeine Einstellungen (Bild)</strong></summary>
<br>

<img src="Images/Einstellungen.png" width="800" alt="Einstellungen">
</details>

| Einstellung | Beschreibung |
| :--- | :--- |
| **Pfade-Einstellungen** | Definieren Sie getrennte Standardpfade für Serien und Filme (Abonnements vs. Manuell). |
| **Temporärer Download-Pfad** | Ein optionaler Ordner zum Zwischenspeichern von Downloads (schont SSDs). |
| **Abo-Standardwerte** | Legen Sie fest, mit welchen Einstellungen neue Abonnements initial erstellt werden. |
| **Wartung** | Aktiviert die automatische Bereinigung ungültiger `.strm`-Dateien (Link-Check). |
| **Suchtiefe & Seitengröße** | Konfigurieren Sie, wie viele Ergebnisse pro API-Anfrage geladen werden und wie viele Seiten maximal durchsucht werden sollen (optimiert die Geschwindigkeit vs. Vollständigkeit). |
| **Untertitel herunterladen** | Aktiviert den automatischen Untertitel-Download. |
| **Minimaler freier Speicherplatz** | Stoppt Downloads bei wenig Speicherplatz (konfigurierbar). |
| **Maximale Bandbreite** | Begrenzung in MBit/s (0 = unbegrenzt). |
| **Parallele Downloads** | Anzahl der gleichzeitig laufenden Downloads (1–10, Standard: 1). |
| **Bibliotheks-Scan** | Aktualisiert die Jellyfin-Bibliothek automatisch nach fertigen Downloads. |
| **MediathekViewWeb API Base-URL** | Basis-URL des MediathekViewWeb Servers (ohne `/api`, die wird automatisch ergänzt). Leer lassen für den Standard (`https://mediathekviewweb.de`). Kann z. B. auf einen lokalen Testserver (`http://localhost:8000`) gesetzt werden, um neue Backend-Features von MediathekViewWeb zu testen. Über den 🔌-Button neben dem Feld wird die Verbindung geprüft (`<Base-URL>/stats` muss mit 200 und "Server is up and running" antworten). |
| **Zapp API Base-URL** | Basis-URL der Zapp API (ohne `/v1`, die wird automatisch ergänzt). Leer lassen für den Standard (`https://api.zapp.mediathekview.de`). Kann z. B. auf einen lokalen Testserver (`http://localhost:3000`) gesetzt werden, um neue Backend-Features der Zapp API zu testen. |

### 📺 Abonnements (Automatische Downloads)
(Tab: *Abo Verwaltung*)

Das Herzstück des Plugins. Hier definieren Sie, was regelmäßig gesucht wird.

<img src="Images/Abos.png" width="800" alt="Abo Übersicht">

<details>
<summary><strong>🔽 Abo-Editor Ansicht (Screenshot)</strong></summary>
<br>
<img src="Images/AbosBearbeiten.png" width="800" alt="Abo Editor">
</details>

<details>
<summary><strong>🔽 Klicken für Details zu allen Abo-Optionen (Tabelle)</strong></summary>

| Option | Beschreibung |
| :--- | :--- |
| **Name** | Der Name des Abos. Bestimmt den Unterordner für die Serie im Zielverzeichnis. |
| **Virtuell (nur Kanal)** | Markiert das Abo als *virtuell*. Es werden **keine Dateien heruntergeladen** und **keine STRMs erstellt**. Die Sendungen erscheinen stattdessen im Jellyfin-**Kanal** *Mediathek (Virtual)* und werden bei der Wiedergabe direkt aus der Mediathek gestreamt. Ideal, um Speicherplatz zu sparen und trotzdem bequem durch die Inhalte zu browsen. |
| **Suchanfragen** | Eine oder mehrere Suchkriterien (Titel, Thema, Sender). <br>• **Ausschluss (NOT):** Klicken Sie auf die `NOT`-Schaltfläche im Abo-Editor, um einen Begriff auszuschließen (rot markiert). Ergebnisse mit diesem Begriff werden ignoriert. |
| **Download-Pfad** | Überschreibt den globalen Standard-Download-Pfad nur für dieses Abo. |
| **Min. / Max. Dauer** | Filtert Ergebnisse anhand der Dauer (in Minuten). |
| **Min. / Max. Datum** | Filtert Ergebnisse anhand des Sendedatums. |
| **Nur Serien herunterladen** | Lädt nur Inhalte, bei denen Staffel und Episode (SxxExx) erkannt wurden (`EnforceSeriesParsing`). |
| **Absolute Nummerierung erlauben** | Erlaubt Episoden wie "Episode 5" statt "S01E05". (Nur aktiv wenn "Nur Serien" aktiv). |
| **Metadaten (.nfo) erstellen** | Generiert NFO-Dateien mit Beschreibungen und Tags für Jellyfin/Kodi. |
| **Originalsprache (ISO)** | Setzt einen ISO-Sprachcode (z.B. 'eng'), wenn der Inhalt als Originalversion erkannt wird. |
| **Streaming (.strm) verwenden** | Speichert keine Videodatei, sondern nur eine Textdatei, die auf den Online-Stream verweist. |
| **Vollständiges Video für sek. Audio** | Lädt das komplette Video, auch wenn es eine andere Sprache als Deutsch hat (sonst nur Audio-Extrakt). (Nicht bei .strm). |
| **Nicht-Episoden als Extras** | Behandelt Videos ohne Episodennummer als Bonusmaterial. |
| ↳ **Trailer speichern** | Speichert Trailer. |
| ↳ **Interviews speichern** | Speichert Interviews. |
| ↳ **Generische Extras speichern** | Speichert sonstige Extras. |
| ↳ **Extras als Stream (.strm)** | Speichert Extras nur als Verknüpfung (spart Speicher). |
| **Audiodeskription erlauben** | Lädt auch Versionen mit Bildbeschreibung herunter. |
| **Gebärdensprache erlauben** | Lädt auch Versionen mit Gebärdensprache herunter. |
| **Erweiterte Duplikaterkennung** | Scannt das Zielverzeichnis physisch nach vorhandenen Dateien (SxxExx), um Doppelte zu vermeiden. |
| **Bevorzugte Reihenfolge** | Legt fest, welche Auflösung bevorzugt wird: *Höchste zuerst* (Standard) oder *Niedrigste zuerst* (spart Speicherplatz). |
| **Min. / Max. Auflösung** | Legt den zulässigen Auflösungsbereich fest (2160p/4K bis 144p). Streams außerhalb des Bereichs werden übersprungen. Für jeden Stream wird die reale Auflösung per ffmpeg ermittelt (verlangsamt den Scan leicht; prüft automatisch, ob die URL noch gültig ist). |
| ↳ **Streng** | Überspringt Streams, deren Auflösung nicht ermittelt werden konnte. Andernfalls wird eine Schätzung anhand der Ziel-Qualität (1080p/720p/480p) verwendet. (Nur aktiv wenn Min. oder Max. Auflösung gesetzt). |
| **Datum/Uhrzeit im Titel** | Hängt das Datum oder die Uhrzeit an den Titel an (ideal für News/Daily). |
| **Abo prüfen (Dry Run)** | Testet die Sucheinstellungen, ohne Dateien herunterzuladen. |

</details>

### 📥 Downloads
(Tab: *Downloads*)

Behalten Sie den Überblick über laufende und vergangene Downloads.

<img src="Images/Downloads.png" width="800" alt="Downloads Übersicht">

*   **Aktive Downloads:** Zeigt den aktuellen Fortschritt, Status und Geschwindigkeit. Laufende Downloads können hier abgebrochen werden.
*   **Historie:** Eine Liste der erfolgreich abgeschlossenen Downloads.

### 📋 Logs
(Tab: *Logs*)

Zeigt die Server-Logs an, gefiltert auf Plugin-Einträge.

<details>
<summary><strong>🔽 Logs Ansicht (Bild)</strong></summary>
<br>

<img src="Images/Logs.png" width="800" alt="Logs Übersicht">
</details>

*   **Log-Datei Auswahl:** Wählen Sie eine beliebige Log-Datei aus dem Dropdown (sortiert nach Änderungsdatum).
*   **Nur MediathekViewDL:** Standardmäßig aktiv — filtert Einträge, die den Plugin-Namespace enthalten.
*   **Suche:** Durchsuchen Sie die geladenen Einträge (case-insensitive). Mit der Regex-Checkbox können reguläre Ausdrücke verwendet werden.
*   **Auto-Scroll:** Hält den automatischen Scroll nach unten beim Aktualisieren bei.
*   **Auto-Update (5s):** Aktualisiert den Log-Inhalt alle 5 Sekunden automatisch.
*   **Kopieren:** Kopiert alle sichtbaren (gefilterten) Einträge in die Zwischenablage.
*   **Doppelklick:** Kopiert einen einzelnen Log-Eintrag (inkl. Stack-Trace) in die Zwischenablage.

---

## ❤️ Danksagung & Disclaimer

*   **Danke:** Ein großer Dank geht an das Team von [MediathekViewWeb.de](https://mediathekviewweb.de/) für die Bereitstellung der API, ohne die dieses Plugin nicht möglich wäre.
*   **Disclaimer:** Dieses Plugin dient der Automatisierung des Zugriffs auf öffentlich verfügbare Inhalte. Bitte beachten Sie die Nutzungsbedingungen der jeweiligen Sender und Mediatheken. Die Nutzung erfolgt auf eigene Gefahr.

---
## Letze Anpassung der Readme
* Plugin: v0.8.0.3
