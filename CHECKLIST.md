# Must never happen

This list names what Nax Explorer must never do. Every release report marks each item as verified by a named test or as not verified. The owner approved this list on 2026-11-04.

1. The app never deletes, overwrites, moves or renames a file or folder unless the user chose that action. Deleting goes to the Recycle Bin; permanent deletion needs Shift and the Windows confirmation.
2. Copy, move, rename and delete never replace an existing item without the Windows conflict dialog, and never move or copy a folder into itself or one of its own subfolders.
3. Reading folders, previews, searches and the AI index never change a user's file, its contents, size or modification time, and never stop other programs from writing, renaming or deleting it.
4. Turning a picture in the viewer never changes the picture file.
5. The app never sends file names, file contents, search text or anything else about the PC to a server. The only network access is the model download the user starts, from a fixed address, and the model is kept only when its SHA-256 hashes match.
6. A damaged settings file or index never stops the app and never touches a user's file; the defaults are used and the index is rebuilt.
7. Saving settings or index entries never leaves a half-written file behind after a crash.
8. Indexing never runs while a full-screen program such as a game runs, when that setting is on, and never on battery, when that setting is on.
9. Reading large folders, searching and indexing never freeze the window.
10. A broken thumbnail handler, a damaged document or a folder that vanishes while it is shown never ends the app.
11. Uninstalling never leaves program files, settings, file associations or other system changes behind, except the index and model if the user chooses to keep them.
