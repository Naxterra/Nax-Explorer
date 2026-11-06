[Setup]
AppName=Nax Explorer cleanup regression tests
AppVersion=1
DefaultDirName={tmp}\NaxExplorerCleanupTests
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputBaseFilename=CleanupTests
Compression=none
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes

[Code]
#include "..\..\installer\DataCleanup.iss"

procedure Check(const Condition: Boolean; const Scenario: String);
begin
  if not Condition then RaiseException('FAIL ' + Scenario);
  Log('PASS ' + Scenario);
end;

procedure Put(const Path, Contents: String);
begin
  if not ForceDirectories(ExtractFileDir(Path)) then RaiseException('Cannot create fixture directory');
  if not SaveStringToFile(Path, Contents, False) then RaiseException('Cannot write fixture');
end;

function Contents(const Path: String): String;
var
  Lines: TArrayOfString;
begin
  Result := '';
  if LoadStringsFromFile(Path, Lines) and (GetArrayLength(Lines) = 1) then Result := Lines[0];
end;

procedure Seed(const Folder: String);
begin
  Put(Folder + '\settings.json', 'settings');
  Put(Folder + '\settings.json.tmp', 'incomplete settings');
  Put(Folder + '\crash.log', 'crash');
  Put(Folder + '\handoff.txt', 'handoff');
  Put(Folder + '\shellwindow-trace.log', 'legacy trace');
  Put(Folder + '\index\index.db', 'index bytes');
  Put(Folder + '\models\model.onnx', 'model bytes');
end;

function InitializeSetup: Boolean;
var
  Root, Folder: String;
begin
  Root := ExpandConstant('{param:FixtureRoot|}');
  if (Root = '') or not DirExists(Root) then RaiseException('FixtureRoot is required');
  Check(HasUninstallSwitch('/KEEPDATA') = (ExpandConstant('{param:ExpectedKeep|0}') = '1'), 'keep_switch_is_explicit_and_case_insensitive');

  Folder := Root + '\complete';
  Seed(Folder);
  CleanupAppData(Folder, True);
  Check(not DirExists(Folder), 'full_cleanup_removes_settings_logs_trace_index_models_and_empty_root');
  CleanupAppData(Folder, True);
  Check(not DirExists(Folder), 'repeated_cleanup_is_harmless');

  Folder := Root + '\keep';
  Seed(Folder);
  CleanupAppData(Folder, False);
  Check(not FileExists(Folder + '\settings.json') and not FileExists(Folder + '\settings.json.tmp') and
    not FileExists(Folder + '\crash.log') and not FileExists(Folder + '\handoff.txt') and
    not FileExists(Folder + '\shellwindow-trace.log'), 'keep_ai_data_still_removes_settings_and_all_owned_logs');
  Check((Contents(Folder + '\index\index.db') = 'index bytes') and
    (Contents(Folder + '\models\model.onnx') = 'model bytes'), 'kept_index_and_model_contents_are_unchanged');

  Folder := Root + '\unknown';
  Seed(Folder);
  Put(Folder + '\user-note.txt', 'keep this');
  CleanupAppData(Folder, True);
  Check((Contents(Folder + '\user-note.txt') = 'keep this') and
    not FileExists(Folder + '\shellwindow-trace.log'), 'cleanup_preserves_unrecognized_files');

  // The PowerShell harness creates index and models as junctions to the outside fixture before this process starts.
  Folder := Root + '\junction';
  CleanupAppData(Folder, True);
  Check(not DirExists(Folder) and (Contents(Root + '\outside\sentinel.txt') = 'outside survives'),
    'cleanup_removes_junctions_without_touching_their_targets');
  Result := True;
end;
