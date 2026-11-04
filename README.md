# Nax Explorer

Nax Explorer is a dual-pane file manager for Windows 11 with a Fluent design, a built-in picture viewer, a preview pane and a search that finds documents by what they are about. The smart search runs entirely on the PC: a small multilingual AI model on the graphics card reads the documents in the folders you choose, and nothing is sent anywhere.

## Features

- Two panes side by side, each with its own tabs, history, address bar with breadcrumbs, and details or thumbnail view. F7 copies the selection to the other pane and F8 moves it there.
- File names coloured by kind (pictures, documents, code, archives and so on), drive usage bars, a Mica window and light or dark theme.
- Copy, move, rename and delete go through the Windows copy engine, so the Windows progress and conflict dialogs appear, deleted items go to the Recycle Bin, and Explorer's Ctrl+Z can undo them. The full Windows context menu with every shell extension is one click away under "Show more options" or Shift+right-click.
- A preview pane shows pictures, thumbnails of videos, PDFs and Office files, the start of text files and the text of Word, Excel, PowerPoint and PDF documents.
- A picture viewer opens with Space or a double-click: arrow keys step through the folder, the mouse wheel zooms around the pointer, dragging pans, R turns the picture for viewing without changing the file, and F11 shows it full screen. It reads every format Windows has a codec for, including HEIC, AVIF and camera RAW files when their free extensions from the Microsoft Store are installed.
- Typing in the search box filters the current folder at once. Enter starts a search of the folder and its subfolders:
  - **Smart** search uses the AI index. It ranks file names that contain every word first, then documents whose text contains the words, merged with documents whose meaning is close to the query. "oracle upgrade" also finds a German runbook about raising a database from 19c to 23ai that never uses those words.
  - **Exact** search scans the folders live, by name (with wildcards such as `*.pdf`) and by content, including the text inside Word, Excel, PowerPoint, OpenDocument and PDF files.

## AI search

AI search is off until you turn it on in the settings. On first use the app downloads the model [intfloat/multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small) (about 475 MB, MIT license) from a fixed revision and keeps the files only when their SHA-256 hashes match the published ones. The model understands German, English and about 100 other languages.

The indexer reads text files, source code, HTML, RTF, Word, Excel, PowerPoint, OpenDocument and PDF files in the indexed folders (Documents, Desktop and Downloads by default; add or remove folders in the settings or with "Add to AI index" on a folder). Other files are recorded by name, so the smart search also finds them by name. It runs on a background thread with lowered priority, uses the graphics card through DirectML or the processor when there is no suitable card, pauses while a game or another full-screen program runs and, if set, on battery, and releases the model from memory after a few idle minutes. It opens files for reading only and never changes them.

The index and the model live in `%LOCALAPPDATA%\Nax-Explorer`. The settings can rebuild the index or delete index and model.

## Keyboard

| Keys | Action |
| --- | --- |
| Enter, double-click | Open |
| Space | View the selected picture |
| Backspace, Alt+Up | Up one level |
| Alt+Left, Alt+Right | Back, forward |
| Ctrl+T, Ctrl+W, Ctrl+Tab | New tab, close tab, next tab |
| Tab (in the list) | Switch to the other pane |
| F7, F8 | Copy, move to the other pane |
| F2 | Rename in place |
| Del, Shift+Del | Delete to the Recycle Bin, delete permanently |
| Ctrl+C, Ctrl+X, Ctrl+V | Copy, cut, paste (works with Explorer too) |
| Ctrl+Shift+C | Copy as path |
| Ctrl+Shift+N | New folder |
| Ctrl+L, Alt+D | Edit the address |
| Ctrl+F, F3 | Search |
| F5 | Refresh |
| F9, Alt+P | Dual pane, preview pane |
| Ctrl+H | Show hidden items |
| Shift+F10, Shift+right-click | Windows context menu |

## Installing

`build-installer.ps1` publishes the app self-contained for x64, collects the notices of every bundled component into THIRD-PARTY-NOTICES.txt and builds `artifacts\installer\Nax-Explorer-<version>-setup.exe` with Inno Setup 7. The installer needs no administrator rights and always installs for the current user only. Uninstalling removes the program, its settings and its crash log, and asks whether the AI index and model should be deleted too.

## Building

The solution needs the .NET 11 SDK and builds for x64:

```
dotnet build NaxExplorer.slnx -c Release -p:Platform=x64
```

The tests are a console program. Tests that need the AI model report SKIP when it has not been downloaded.

```
dotnet run --project tests/NaxExplorer.Tests
```

## Projects

- `src/NaxExplorer.Core`: folder listing, sorting, paths and names, file kinds, text detection and extraction, live search and settings.
- `src/NaxExplorer.Search`: the AI index in SQLite (full-text search with FTS5 and one vector per text piece), the ONNX Runtime embedder, the indexer and the hybrid ranking.
- `src/NaxExplorer`: the WinUI 3 app.
- `tests/NaxExplorer.Tests`: the tests.

## License

This software is under the Nax Source-Available License 1.0. You may install and use the official release builds, privately and at work, and you may read the source code. Modifying the software, redistributing it and selling it need the written permission of the owner. See [LICENSE](LICENSE).
