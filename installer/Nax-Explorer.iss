; Installer for Nax Explorer. Build it with build-installer.ps1, which publishes the app and passes AppVersion and
; PublishDir to this script.

#ifndef AppVersion
  #error AppVersion is not defined. Run build-installer.ps1 instead of compiling this script directly.
#endif
#ifndef PublishDir
  #error PublishDir is not defined. Run build-installer.ps1 instead of compiling this script directly.
#endif

#define AppName "Nax Explorer"
#define AppExe "Nax-Explorer.exe"

[Setup]
AppId={{3E8B5C2D-7A41-4F96-B0D3-9C6E1A4F2B87}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Naxterra
DefaultDirName={autopf}\Nax-Explorer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The app never needs administrator rights, and it is always installed for the current user only. An installation for
; all users is not offered: its uninstaller would run in one account and could not remove the settings, index and model
; that the app keeps in every other user's profile.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Nax-Explorer-{#AppVersion}-setup
SetupIconFile=..\src\NaxExplorer\Assets\AppIcon.ico
; The license is shown for information with a Next button. Installing and running the app is what section 1 allows;
; the restrictions in sections 2 to 7 apply through copyright, not through a click.
InfoBeforeFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
; The app holds these mutexes while it runs, the global one also visible from other Windows sessions. Setup and the
; uninstaller wait until it is closed, so no locked program file is left behind.
AppMutex=NaxExplorer.Running,Global\NaxExplorer.Running

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.DesktopTask=Create a desktop shortcut
german.DesktopTask=Desktopverknüpfung erstellen
english.LaunchApp=Start {#AppName}
german.LaunchApp={#AppName} starten
english.DataQuestion={#AppName} keeps its AI index and the downloaded AI model in %1 (%2 MB).%n%nDo you want to delete them as well? Choose No to keep them for a later installation.
german.DataQuestion={#AppName} bewahrt seinen KI-Index und das heruntergeladene KI-Modell in %1 auf (%2 MB).%n%nMöchten Sie diese ebenfalls löschen? Wählen Sie Nein, um sie für eine spätere Installation zu behalten.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked

[Files]
; Debug symbols are left out; they only help a debugger and would add several megabytes.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
; The same taskbar ID the app sets for itself, so a pinned shortcut and the running app share one taskbar button.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Naxterra.NaxExplorer"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Naxterra.NaxExplorer"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
// The app keeps its settings, its crash log, the AI index and the AI model in this folder of the user's local
// application data. Nothing else of the app lives outside the installation folder.
function DataFolder: String;
begin
  Result := ExpandConstant('{localappdata}\Nax-Explorer');
end;

// The size of a folder and everything in it, in bytes. Junctions and symbolic links are not followed, so a link that
// points back to a parent folder can never make this loop or count the same files twice.
function FolderSize(const Folder: String): Int64;
var
  Find: TFindRec;
begin
  Result := 0;
  if FindFirst(Folder + '\*', Find) then
  try
    repeat
      if (Find.Name = '.') or (Find.Name = '..') then Continue;
      if (Find.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then Continue;
      if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        Result := Result + FolderSize(Folder + '\' + Find.Name)
      else
        Result := Result + (Int64(Find.SizeHigh) shl 32) + Find.SizeLow;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Size: Int64;
begin
  if (CurUninstallStep <> usPostUninstall) or not DirExists(DataFolder) then Exit;
  // Settings and the crash log always go.
  DeleteFile(DataFolder + '\settings.json');
  DeleteFile(DataFolder + '\settings.json.tmp');
  DeleteFile(DataFolder + '\crash.log');
  // The index and the model are large and can be kept for a later installation. A silent uninstall removes them, so
  // nothing is left behind.
  if DirExists(DataFolder + '\index') or DirExists(DataFolder + '\models') then
  begin
    Size := (FolderSize(DataFolder + '\index') + FolderSize(DataFolder + '\models')) div (1024 * 1024);
    if UninstallSilent or
      (MsgBox(FmtMessage(CustomMessage('DataQuestion'), [DataFolder, IntToStr(Size)]), mbConfirmation, MB_YESNO) = IDYES) then
    begin
      DelTree(DataFolder + '\index', True, True, True);
      DelTree(DataFolder + '\models', True, True, True);
    end;
  end;
  // Removed only when nothing else is left in it.
  RemoveDir(DataFolder);
end;
