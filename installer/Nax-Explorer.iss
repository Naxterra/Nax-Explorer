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
; Uninstall restores folder verbs from code; Explorer must discard its cached association information afterward.
ChangesAssociations=yes
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
#include "DataCleanup.iss"

// The app keeps its settings, its crash log, the AI index and the AI model in this folder of the user's local
// application data. Outside the installation folder there are only these files and, while "open folders in Nax
// Explorer" is on, its entries under HKEY_CURRENT_USER\Software\Classes and its sign-in entry under ...\Run, which the
// uninstall step below removes.
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

const
  ExplorerVerb = 'openinnaxexplorer';
  WinEKey = 'Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow';

// Deletes a registry key of the current user when it has neither values nor subkeys left.
procedure DeleteKeyIfEmpty(const Key: String);
var
  Names: TArrayOfString;
begin
  if not RegKeyExists(HKCU, Key) then Exit;
  if RegGetSubkeyNames(HKCU, Key, Names) and (GetArrayLength(Names) > 0) then Exit;
  if RegGetValueNames(HKCU, Key, Names) and (GetArrayLength(Names) > 0) then Exit;
  RegDeleteKeyIfEmpty(HKCU, Key);
end;

// Gives a folder type back to Explorer, the same way the app does when the setting is turned off: the default verb
// that was there before is put back, and the app's verb is removed.
procedure ReleaseFolderType(const FolderType: String);
var
  Shell, Current, Previous: String;
begin
  Shell := 'Software\Classes\' + FolderType + '\shell';
  if RegQueryStringValue(HKCU, Shell, '', Current) and (Current = ExplorerVerb) then
  begin
    if RegQueryStringValue(HKCU, Shell + '\' + ExplorerVerb, 'NaxExplorerPreviousDefault', Previous) then
      RegWriteStringValue(HKCU, Shell, '', Previous)
    else
      RegDeleteValue(HKCU, Shell, '');
  end;
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + '\' + ExplorerVerb);
  DeleteKeyIfEmpty(Shell);
  DeleteKeyIfEmpty('Software\Classes\' + FolderType);
end;

// Removes the notes the app keeps on a verb key it took over.
procedure DeleteNotes(const VerbKey: String);
begin
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorerPreviousCommand');
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorerPreviousDelegateExecute');
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorerCommand');
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorerCreatedCommand');
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorerFinished');
  RegDeleteValue(HKCU, VerbKey, 'NaxExplorer');
end;

// Gives one command the app took over back, the same way the app does when the setting is turned off: a key the app
// made is removed, a command the app replaced is put back, and a command another program wrote after the app is left
// alone, so only the app's notes go. Returns True when it removed a key the app made, so empty parent keys can go too.
function ReleaseCommand(const VerbKey: String): Boolean;
var
  Marker, Value, Own, Current: String;
  StillOwn: Boolean;
begin
  Result := False;
  if not RegQueryStringValue(HKCU, VerbKey, 'NaxExplorer', Marker) then Exit;
  if not RegQueryStringValue(HKCU, VerbKey, 'NaxExplorerCommand', Own) then Own := '';
  if not RegQueryStringValue(HKCU, VerbKey + '\command', '', Current) then Current := '';
  // A build from before the app noted its command wrote only the Win+E command; then a command that runs the app counts.
  if Own <> '' then
    StillOwn := Current = Own
  else
    StillOwn := Pos('nax-explorer.exe', Lowercase(Current)) > 0;
  if not StillOwn then
  begin
    DeleteNotes(VerbKey);
    Exit;
  end;
  if Marker = 'created' then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, VerbKey);
    Result := True;
    Exit;
  end;
  if RegQueryStringValue(HKCU, VerbKey, 'NaxExplorerPreviousCommand', Value) then
    RegWriteStringValue(HKCU, VerbKey + '\command', '', Value)
  else
    RegDeleteValue(HKCU, VerbKey + '\command', '');
  if RegQueryStringValue(HKCU, VerbKey, 'NaxExplorerPreviousDelegateExecute', Value) then
    RegWriteStringValue(HKCU, VerbKey + '\command', 'DelegateExecute', Value)
  else
    RegDeleteValue(HKCU, VerbKey + '\command', 'DelegateExecute');
  if RegValueExists(HKCU, VerbKey, 'NaxExplorerCreatedCommand') then DeleteKeyIfEmpty(VerbKey + '\command');
  DeleteNotes(VerbKey);
end;

const
  // The hidden window the app keeps while it runs in the background; it must match BackgroundWindow.Title in the app.
  BackgroundTitle = 'Nax Explorer Background';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'Nax Explorer';
  WM_CLOSE = $0010;

// While the app opens folders instead of Explorer it runs in the background without a window, so the user could not
// close it for setup. It is asked to end through its hidden window, and setup waits up to ten seconds for that.
procedure CloseBackgroundApp;
var
  Wnd: HWND;
  Tries: Integer;
begin
  Wnd := FindWindowByWindowName(BackgroundTitle);
  if Wnd = 0 then Exit;
  PostMessage(Wnd, WM_CLOSE, 0, 0);
  Tries := 0;
  while (FindWindowByWindowName(BackgroundTitle) <> 0) and (Tries < 100) do
  begin
    Sleep(100);
    Tries := Tries + 1;
  end;
  // The hidden window goes first; the app still saves its state and closes its index after it. Setup waits until the
  // app's mutex is free as well, so no program file or index file is still open when setup goes on.
  Tries := 0;
  while CheckForMutexes('NaxExplorer.Running') and (Tries < 100) do
  begin
    Sleep(100);
    Tries := Tries + 1;
  end;
end;

function InitializeSetup: Boolean;
begin
  CloseBackgroundApp;
  Result := True;
end;

function InitializeUninstall: Boolean;
begin
  CloseBackgroundApp;
  Result := True;
end;

// Removes the sign-in entry, but only the app's own: a value of the same name another program wrote stays.
procedure RemoveStartAtSignIn;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) and (Pos('nax-explorer.exe', Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Size: Int64;
  RemoveAiData: Boolean;
begin
  // If the app opened folders instead of Explorer, Explorer gets them back before the app's files are gone, and the
  // app no longer starts at sign-in.
  if CurUninstallStep = usUninstall then
  begin
    RemoveStartAtSignIn;
    RemovePinnedShortcuts(ExpandConstant('{userprograms}'));
    ReleaseFolderType('Directory');
    ReleaseFolderType('Drive');
    if ReleaseCommand(WinEKey) then
    begin
      DeleteKeyIfEmpty('Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell');
      DeleteKeyIfEmpty('Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}');
    end;
    ReleaseCommand('Software\Classes\Folder\shell\open');
    ReleaseCommand('Software\Classes\Folder\shell\explore');
    ReleaseCommand('Software\Classes\CompressedFolder\shell\open');
    DeleteKeyIfEmpty('Software\Classes\CompressedFolder\shell');
    DeleteKeyIfEmpty('Software\Classes\CompressedFolder');
    DeleteKeyIfEmpty('Software\Classes\Folder\shell');
    DeleteKeyIfEmpty('Software\Classes\Folder');
    Exit;
  end;
  if (CurUninstallStep <> usPostUninstall) or not DirExists(DataFolder) then Exit;
  // The index and the model are large and can be kept for a later installation. A silent uninstall removes them, so
  // nothing is left behind, unless /KEEPDATA explicitly requests retaining only the index and model.
  RemoveAiData := not HasUninstallSwitch('/KEEPDATA');
  if RemoveAiData and not UninstallSilent and
    (DirExists(DataFolder + '\index') or DirExists(DataFolder + '\models')) then
  begin
    Size := (FolderSize(DataFolder + '\index') + FolderSize(DataFolder + '\models')) div (1024 * 1024);
    RemoveAiData := MsgBox(FmtMessage(CustomMessage('DataQuestion'), [DataFolder, IntToStr(Size)]), mbConfirmation, MB_YESNO) = IDYES;
  end;
  CleanupAppData(DataFolder, RemoveAiData);
end;
