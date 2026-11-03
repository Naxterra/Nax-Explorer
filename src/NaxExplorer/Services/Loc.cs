using System.Globalization;
using Microsoft.UI.Xaml.Markup;

namespace NaxExplorer;

/// <summary>A text in the interface language, for XAML: {local:T Key=Open}.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}

/// <summary>
/// Interface texts in English and German. German is used when it is chosen in the settings or, with no choice, when
/// Windows shows German. Folder names, file types and messages from Windows stay as Windows provides them.
/// </summary>
internal static class Loc
{
    private static bool _german;

    public static void Apply(string setting)
    {
        _german = setting == "de-DE" || (setting.Length == 0 && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de");
        if (setting.Length > 0)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(setting);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    public static string T(string key) => Texts.TryGetValue(key, out var text) ? (_german ? text.De : text.En) : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static string Drive(DriveType type) => type switch
    {
        DriveType.Removable => T("DriveRemovable"),
        DriveType.Network => T("DriveNetwork"),
        DriveType.CDRom => T("DriveOptical"),
        DriveType.Ram => T("DriveRam"),
        _ => T("DriveLocal"),
    };

    private static readonly Dictionary<string, (string En, string De)> Texts = new()
    {
        // Places
        ["ThisPC"] = ("This PC", "Dieser PC"),
        ["Home"] = ("Home", "Start"),
        ["Desktop"] = ("Desktop", "Desktop"),
        ["Downloads"] = ("Downloads", "Downloads"),
        ["Documents"] = ("Documents", "Dokumente"),
        ["Pictures"] = ("Pictures", "Bilder"),
        ["Music"] = ("Music", "Musik"),
        ["Videos"] = ("Videos", "Videos"),
        ["RecycleBin"] = ("Recycle Bin", "Papierkorb"),
        ["QuickAccess"] = ("QUICK ACCESS", "SCHNELLZUGRIFF"),
        ["Pinned"] = ("PINNED", "ANGEHEFTET"),
        ["DrivesHeader"] = ("THIS PC", "DIESER PC"),
        ["DriveLocal"] = ("Local Disk", "Lokaler Datenträger"),
        ["DriveRemovable"] = ("Removable Disk", "Wechseldatenträger"),
        ["DriveNetwork"] = ("Network Drive", "Netzlaufwerk"),
        ["DriveOptical"] = ("CD/DVD Drive", "CD/DVD-Laufwerk"),
        ["DriveRam"] = ("RAM Disk", "RAM-Datenträger"),
        ["DriveFree"] = ("{0} free of {1}", "{0} frei von {1}"),
        ["FreeSpace"] = ("{0} free", "{0} frei"),

        // Toolbar and commands
        ["New"] = ("New", "Neu"),
        ["NewFolder"] = ("Folder", "Ordner"),
        ["NewTextFile"] = ("Text document", "Textdokument"),
        ["NewWindow"] = ("New window", "Neues Fenster"),
        ["NewFolderName"] = ("New folder", "Neuer Ordner"),
        ["NewTextFileName"] = ("New text document", "Neues Textdokument"),
        ["CutTip"] = ("Cut (Ctrl+X)", "Ausschneiden (Strg+X)"),
        ["CopyTip"] = ("Copy (Ctrl+C)", "Kopieren (Strg+C)"),
        ["PasteTip"] = ("Paste (Ctrl+V)", "Einfügen (Strg+V)"),
        ["RenameTip"] = ("Rename (F2)", "Umbenennen (F2)"),
        ["DeleteTip"] = ("Delete (Del). With Shift, delete permanently.", "Löschen (Entf). Mit Umschalt endgültig löschen."),
        ["CopyToOtherShort"] = ("Copy to", "Kopieren nach"),
        ["MoveToOtherShort"] = ("Move to", "Verschieben nach"),
        ["CopyToOtherTip"] = ("Copy the selection to the other pane (F7)", "Auswahl in den anderen Bereich kopieren (F7)"),
        ["MoveToOtherTip"] = ("Move the selection to the other pane (F8)", "Auswahl in den anderen Bereich verschieben (F8)"),
        ["View"] = ("View", "Ansicht"),
        ["ViewDetails"] = ("Details", "Details"),
        ["ViewThumbnails"] = ("Thumbnails", "Miniaturansichten"),
        ["ShowHidden"] = ("Show hidden items", "Ausgeblendete Elemente anzeigen"),
        ["ShowExtensions"] = ("Show file name extensions", "Dateinamenerweiterungen anzeigen"),
        ["ColorfulNames"] = ("Colour names by file type", "Namen nach Dateityp einfärben"),
        ["DualPane"] = ("Dual pane", "Zwei Bereiche"),
        ["DualPaneTip"] = ("Show two folders side by side (F9)", "Zwei Ordner nebeneinander anzeigen (F9)"),
        ["Preview"] = ("Preview", "Vorschau"),
        ["PreviewTip"] = ("Show the preview pane (Alt+P)", "Vorschaufenster anzeigen (Alt+P)"),
        ["SettingsTip"] = ("Settings (Ctrl+,)", "Einstellungen (Strg+,)"),
        ["BackTip"] = ("Back (Alt+Left)", "Zurück (Alt+Links)"),
        ["ForwardTip"] = ("Forward (Alt+Right)", "Vor (Alt+Rechts)"),
        ["UpTip"] = ("Up one level (Alt+Up)", "Eine Ebene höher (Alt+Oben)"),
        ["RefreshTip"] = ("Refresh (F5)", "Aktualisieren (F5)"),

        // Columns and sorting
        ["ColumnName"] = ("Name", "Name"),
        ["ColumnModified"] = ("Date modified", "Änderungsdatum"),
        ["ColumnType"] = ("Type", "Typ"),
        ["ColumnSize"] = ("Size", "Größe"),
        ["SortBy"] = ("Sort by", "Sortieren nach"),
        ["Descending"] = ("Descending", "Absteigend"),

        // Context menu
        ["Open"] = ("Open", "Öffnen"),
        ["OpenFolder"] = ("Open folder", "Ordner öffnen"),
        ["ViewPicture"] = ("View picture", "Bild anzeigen"),
        ["OpenInNewTab"] = ("Open in new tab", "In neuem Tab öffnen"),
        ["OpenInOtherPane"] = ("Open in other pane", "Im anderen Bereich öffnen"),
        ["OpenWith"] = ("Open with…", "Öffnen mit…"),
        ["OpenLocation"] = ("Open file location", "Dateipfad öffnen"),
        ["Cut"] = ("Cut", "Ausschneiden"),
        ["Copy"] = ("Copy", "Kopieren"),
        ["Paste"] = ("Paste", "Einfügen"),
        ["PasteInto"] = ("Paste into folder", "In Ordner einfügen"),
        ["CopyPath"] = ("Copy as path", "Als Pfad kopieren"),
        ["CopyToOther"] = ("Copy to other pane", "In anderen Bereich kopieren"),
        ["MoveToOther"] = ("Move to other pane", "In anderen Bereich verschieben"),
        ["Rename"] = ("Rename", "Umbenennen"),
        ["Delete"] = ("Delete", "Löschen"),
        ["PinToSidebar"] = ("Pin to sidebar", "An Seitenleiste anheften"),
        ["Unpin"] = ("Unpin", "Lösen"),
        ["AddToAiIndex"] = ("Add to AI index", "Zum KI-Index hinzufügen"),
        ["Properties"] = ("Properties", "Eigenschaften"),
        ["MoreOptions"] = ("Show more options", "Weitere Optionen anzeigen"),
        ["Refresh"] = ("Refresh", "Aktualisieren"),
        ["OpenTerminal"] = ("Open in Terminal", "Im Terminal öffnen"),

        // List and status
        ["ItemCount"] = ("{0:N0} items", "{0:N0} Elemente"),
        ["Selected"] = ("{0:N0} selected", "{0:N0} ausgewählt"),
        ["SelectedWithSize"] = ("{0:N0} selected ({1})", "{0:N0} ausgewählt ({1})"),
        ["SelectedCount"] = ("{0:N0} items selected", "{0:N0} Elemente ausgewählt"),
        ["EmptyFolder"] = ("This folder is empty.", "Dieser Ordner ist leer."),
        ["NothingMatchesFilter"] = ("Nothing here matches the filter. Press Enter to search the subfolders too.", "Nichts hier passt zum Filter. Mit der Eingabetaste werden auch die Unterordner durchsucht."),
        ["AccessDenied"] = ("You do not have permission to open this folder.", "Sie haben keine Berechtigung, diesen Ordner zu öffnen."),
        ["FolderNotFound"] = ("\"{0}\" was not found.", "„{0}“ wurde nicht gefunden."),
        ["OpenFailed"] = ("{0} could not be opened.", "{0} konnte nicht geöffnet werden."),
        ["ClipboardBusy"] = ("The clipboard is in use by another program. Please try again.", "Die Zwischenablage wird von einem anderen Programm verwendet. Bitte erneut versuchen."),
        ["CutDone"] = ("{0:N0} items cut. Paste them where they should go.", "{0:N0} Elemente ausgeschnitten. Am Ziel einfügen."),
        ["CopyDone"] = ("{0:N0} items copied.", "{0:N0} Elemente kopiert."),
        ["PathCopied"] = ("Path copied.", "Pfad kopiert."),
        ["OtherPaneNoFolder"] = ("The other pane does not show a folder to copy to.", "Der andere Bereich zeigt keinen Ordner, in den kopiert werden kann."),
        ["IntoItself"] = ("A folder cannot be copied or moved into itself.", "Ein Ordner kann nicht in sich selbst kopiert oder verschoben werden."),
        ["DropCopy"] = ("Copy to {0}", "Nach {0} kopieren"),
        ["DropMove"] = ("Move to {0}", "Nach {0} verschieben"),
        ["NameEmpty"] = ("A name cannot be empty.", "Ein Name darf nicht leer sein."),
        ["NameInvalidCharacters"] = ("A name cannot contain any of these characters: \\ / : * ? \" < > |", "Ein Name darf keines dieser Zeichen enthalten: \\ / : * ? \" < > |"),
        ["NameReservedName"] = ("Windows reserves this name for devices.", "Windows reserviert diesen Namen für Geräte."),
        ["NameEndsWithDotOrSpace"] = ("A name cannot end with a dot or a space.", "Ein Name darf nicht mit einem Punkt oder Leerzeichen enden."),
        ["NameTooLong"] = ("The name is too long.", "Der Name ist zu lang."),
        ["Name"] = ("Name", "Name"),
        ["SearchResults"] = ("Search results", "Suchergebnisse"),

        // Search
        ["SearchPlaceholder"] = ("Filter or search", "Filtern oder suchen"),
        ["SearchBoxTip"] = ("Typing filters this folder. Enter searches the subfolders too, by name and content.", "Tippen filtert diesen Ordner. Die Eingabetaste durchsucht auch die Unterordner nach Name und Inhalt."),
        ["SmartSearch"] = ("Smart", "Smart"),
        ["ExactSearch"] = ("Exact", "Exakt"),
        ["SmartSearchTip"] = ("Finds files by name, by the words in them and by what they are about, using the local AI index.", "Findet Dateien nach Name, nach den enthaltenen Wörtern und nach ihrem Inhalt, mit dem lokalen KI-Index."),
        ["SmartSearchOffTip"] = ("Turn on AI search in the settings to search by meaning.", "KI-Suche in den Einstellungen einschalten, um nach Bedeutung zu suchen."),
        ["InNames"] = ("Names", "Namen"),
        ["InContents"] = ("Contents", "Inhalte"),
        ["InSubfolders"] = ("Subfolders", "Unterordner"),
        ["StopSearch"] = ("Stop", "Anhalten"),
        ["CloseSearch"] = ("Close the search (Esc)", "Suche schließen (Esc)"),
        ["Searching"] = ("Searching…", "Suche läuft…"),
        ["LoadingModel"] = ("Name and word matches are shown; the AI model is loading for matches by meaning…", "Treffer nach Name und Wörtern werden angezeigt; das KI-Modell lädt für Treffer nach Bedeutung…"),
        ["SearchProgress"] = ("Searching… {0} files looked at, {1} found", "Suche läuft… {0} Dateien geprüft, {1} gefunden"),
        ["SearchDone"] = ("{0} results in {1}", "{0} Ergebnisse in {1}"),
        ["SearchStopped"] = ("Stopped: {0} results in {1}", "Angehalten: {0} Ergebnisse in {1}"),
        ["SearchLimit"] = ("The search stopped at its limit of results.", "Die Suche hat bei der Höchstzahl an Ergebnissen angehalten."),
        ["SearchFailed"] = ("The search failed: {0}", "Die Suche ist fehlgeschlagen: {0}"),
        ["ResultCount"] = ("{0:N0} results", "{0:N0} Ergebnisse"),
        ["NoResults"] = ("Nothing found.", "Nichts gefunden."),
        ["EngineSmart"] = ("✨ AI index", "✨ KI-Index"),
        ["EngineLive"] = ("Live scan", "Live-Suche"),
        ["MatchName"] = ("name", "Name"),
        ["MatchWords"] = ("words", "Wörter"),
        ["MatchMeaning"] = ("meaning", "Bedeutung"),
        ["PartialCoverage"] = ("Only the indexed parts of this folder are searched by meaning.", "Nur die indizierten Teile dieses Ordners werden nach Bedeutung durchsucht."),
        ["AddFolderToAiIndex"] = ("✨ Add this folder to the AI index", "✨ Diesen Ordner zum KI-Index hinzufügen"),
        ["AddedToIndex"] = ("{0} is added to the AI index. It is searchable by meaning once indexing is done.", "{0} wird zum KI-Index hinzugefügt. Nach der Indizierung ist er nach Bedeutung durchsuchbar."),

        // AI card
        ["AiSearch"] = ("AI search", "KI-Suche"),
        ["AiOffHint"] = ("Off. Click to find documents by what they are about, fully on this PC.", "Aus. Klicken, um Dokumente nach ihrem Inhalt zu finden, vollständig auf diesem PC."),
        ["AiNeedsModel"] = ("The model still needs to be downloaded. Click to open the settings.", "Das Modell muss noch heruntergeladen werden. Klicken, um die Einstellungen zu öffnen."),
        ["AiStarting"] = ("Starting…", "Wird gestartet…"),
        ["AiFailed"] = ("Stopped by an error: {0}", "Durch einen Fehler angehalten: {0}"),
        ["AiDocuments"] = ("{0} documents, {1} files", "{0} Dokumente, {1} Dateien"),
        ["AiScanning"] = ("Looking for changes…", "Sucht nach Änderungen…"),
        ["AiIndexing"] = ("Reading {0} documents", "Liest {0} Dokumente"),
        ["AiReady"] = ("Ready", "Bereit"),
        ["AiPauseduser"] = ("Paused", "Angehalten"),
        ["AiPausedfullscreen"] = ("Paused while a full-screen program runs", "Pausiert, solange ein Vollbildprogramm läuft"),
        ["AiPausedbattery"] = ("Paused on battery", "Im Akkubetrieb pausiert"),
        ["PauseIndexing"] = ("Pause indexing", "Indizierung anhalten"),
        ["ResumeIndexing"] = ("Resume indexing", "Indizierung fortsetzen"),

        // Preview
        ["DetailType"] = ("Type", "Typ"),
        ["DetailSize"] = ("Size", "Größe"),
        ["DetailSpace"] = ("Space", "Speicher"),
        ["DetailModified"] = ("Modified", "Geändert"),
        ["DetailCreated"] = ("Created", "Erstellt"),
        ["DetailFolder"] = ("Folder", "Ordner"),
        ["DetailDimensions"] = ("Dimensions", "Abmessungen"),
        ["DetailContains"] = ("Contains", "Enthält"),
        ["DetailNote"] = ("Note", "Hinweis"),
        ["Contains"] = ("{0:N0} folders, {1:N0} files", "{0:N0} Ordner, {1:N0} Dateien"),
        ["ContainsMore"] = ("more than {0:N0} folders and {1:N0} files", "mehr als {0:N0} Ordner und {1:N0} Dateien"),

        // Viewer
        ["ZoomInTip"] = ("Zoom in (+)", "Vergrößern (+)"),
        ["ZoomOutTip"] = ("Zoom out (−)", "Verkleinern (−)"),
        ["FitTip"] = ("Fit to window (0)", "An Fenster anpassen (0)"),
        ["ActualSizeTip"] = ("Actual size (1)", "Originalgröße (1)"),
        ["RotateLeftTip"] = ("Turn left (Shift+R). The file is not changed.", "Nach links drehen (Umschalt+R). Die Datei wird nicht geändert."),
        ["RotateRightTip"] = ("Turn right (R). The file is not changed.", "Nach rechts drehen (R). Die Datei wird nicht geändert."),
        ["FullScreenTip"] = ("Full screen (F11)", "Vollbild (F11)"),
        ["OpenExternalTip"] = ("Open with the default app (Enter)", "Mit der Standard-App öffnen (Eingabe)"),
        ["CloseViewerTip"] = ("Close (Esc)", "Schließen (Esc)"),
        ["PreviousTip"] = ("Previous picture (Left)", "Vorheriges Bild (Links)"),
        ["NextTip"] = ("Next picture (Right)", "Nächstes Bild (Rechts)"),
        ["PictureFailed"] = ("This picture could not be shown: {0}", "Dieses Bild kann nicht angezeigt werden: {0}"),
        ["CodecHeif"] = ("HEIC pictures need the free \"HEIF Image Extensions\" from the Microsoft Store.", "HEIC-Bilder benötigen die kostenlosen „HEIF-Bilderweiterungen“ aus dem Microsoft Store."),
        ["CodecAv1"] = ("AVIF pictures need the free \"AV1 Video Extension\" from the Microsoft Store.", "AVIF-Bilder benötigen die kostenlose „AV1 Video Extension“ aus dem Microsoft Store."),
        ["CodecJxl"] = ("JPEG XL pictures need the \"JPEG XL Image Extension\" from the Microsoft Store.", "JPEG-XL-Bilder benötigen die „JPEG XL Image Extension“ aus dem Microsoft Store."),
        ["CodecRaw"] = ("Camera RAW pictures need the free \"Raw Image Extension\" from the Microsoft Store.", "Kamera-RAW-Bilder benötigen die kostenlose „Raw Image Extension“ aus dem Microsoft Store."),

        // Settings
        ["Settings"] = ("Settings", "Einstellungen"),
        ["Close"] = ("Close", "Schließen"),
        ["SectionGeneral"] = ("General", "Allgemein"),
        ["SectionAi"] = ("AI search", "KI-Suche"),
        ["Language"] = ("Language", "Sprache"),
        ["LanguageSystem"] = ("Same as Windows", "Wie Windows"),
        ["LanguageEnglish"] = ("English", "English"),
        ["LanguageGerman"] = ("Deutsch", "Deutsch"),
        ["LanguageAfterRestart"] = ("The new language applies after Nax Explorer is restarted.", "Die neue Sprache gilt nach einem Neustart von Nax Explorer."),
        ["Theme"] = ("Theme", "Design"),
        ["ThemeSystem"] = ("Same as Windows", "Wie Windows"),
        ["ThemeLight"] = ("Light", "Hell"),
        ["ThemeDark"] = ("Dark", "Dunkel"),
        ["OpenPicturesInViewer"] = ("Open pictures in the built-in viewer", "Bilder im integrierten Betrachter öffnen"),
        ["AiExplain"] = ("AI search finds documents by what they are about, not only by the exact words: \"oracle upgrade\" also finds a runbook that never uses those words. It reads text files, source code, Word, Excel, PowerPoint, OpenDocument and PDF files in the folders below and keeps its index on this PC. Nothing is sent anywhere; the model runs on your graphics card or processor.", "Die KI-Suche findet Dokumente nach ihrem Inhalt, nicht nur nach den genauen Wörtern: „oracle upgrade“ findet auch ein Runbook, das diese Wörter nie verwendet. Sie liest Textdateien, Quellcode sowie Word-, Excel-, PowerPoint-, OpenDocument- und PDF-Dateien in den unten genannten Ordnern und speichert ihren Index auf diesem PC. Es wird nichts gesendet; das Modell läuft auf Ihrer Grafikkarte oder dem Prozessor."),
        ["AiSwitch"] = ("Search by meaning", "Nach Bedeutung suchen"),
        ["AiOffState"] = ("AI search is off. Nothing is indexed.", "Die KI-Suche ist aus. Es wird nichts indiziert."),
        ["AiModelMissing"] = ("The model ({0}) is downloaded once from Hugging Face and checked against its published fingerprint.", "Das Modell ({0}) wird einmalig von Hugging Face geladen und mit seinem veröffentlichten Fingerabdruck geprüft."),
        ["AiModelReady"] = ("The model is installed. Runs on: {0}", "Das Modell ist installiert. Läuft auf: {0}"),
        ["DeviceNotLoaded"] = ("loaded on first use", "wird bei Bedarf geladen"),
        ["DownloadModel"] = ("Download the model ({0})", "Modell herunterladen ({0})"),
        ["Downloading"] = ("Downloading… {0} of {1}", "Wird heruntergeladen… {0} von {1}"),
        ["DownloadFailed"] = ("The download failed: {0}", "Der Download ist fehlgeschlagen: {0}"),
        ["IndexedFolders"] = ("Indexed folders", "Indizierte Ordner"),
        ["AddFolder"] = ("Add folder…", "Ordner hinzufügen…"),
        ["RemoveFolder"] = ("Remove from the index", "Aus dem Index entfernen"),
        ["UseGpu"] = ("Use the graphics card", "Grafikkarte verwenden"),
        ["PauseFullScreen"] = ("Pause indexing while a game or other full-screen program runs", "Indizierung anhalten, solange ein Spiel oder anderes Vollbildprogramm läuft"),
        ["PauseBattery"] = ("Pause indexing on battery", "Indizierung im Akkubetrieb anhalten"),
        ["Rescan"] = ("Look for changes now", "Jetzt nach Änderungen suchen"),
        ["DeleteIndex"] = ("Rebuild the index", "Index neu aufbauen"),
        ["DeleteIndexAndModel"] = ("Delete index and model", "Index und Modell löschen"),
        ["AiDataLocation"] = ("Index and model are kept in {0}.", "Index und Modell liegen in {0}."),
        ["LicenseNote"] = ("Nax Source-Available License 1.0", "Nax Source-Available License 1.0"),
        ["StartFailed"] = ("Nax Explorer could not start. The details are in {0}.", "Nax Explorer konnte nicht starten. Die Einzelheiten stehen in {0}."),
    };
}
