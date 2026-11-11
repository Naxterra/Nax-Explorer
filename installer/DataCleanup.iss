// Shared with the installer regression executable, which exercises the real cleanup on disposable folders.
function HasUninstallSwitch(const Switch: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Switch) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure CleanupAppData(const Folder: String; const RemoveAiData: Boolean);
begin
  // Only files owned by the app are removed. In particular, include the trace produced by earlier builds.
  DeleteFile(Folder + '\settings.json');
  DeleteFile(Folder + '\settings.json.tmp');
  DeleteFile(Folder + '\crash.log');
  DeleteFile(Folder + '\handoff.txt');
  DeleteFile(Folder + '\shellwindow-trace.log');
  if RemoveAiData then
  begin
    // DelTree removes a directory link itself without following it to files outside the app's data folder.
    DelTree(Folder + '\index', True, True, True);
    DelTree(Folder + '\models', True, True, True);
  end;
  // Unknown files are preserved; an empty app data directory is removed.
  RemoveDir(Folder);
end;

// "Pin to Nax-StartMenu" puts its shortcuts in this one folder of the user's Start Menu programs, because Nax-StartMenu
// only accepts pins from there. The folder belongs to the app alone; the user's other Start Menu entries stay.
procedure RemovePinnedShortcuts(const ProgramsFolder: String);
begin
  if ProgramsFolder = '' then Exit;
  DelTree(ProgramsFolder + '\Nax pinned apps', True, True, True);
end;
