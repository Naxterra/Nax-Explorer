namespace NaxExplorer.Core;

public enum EntryKind
{
    File,
    Folder,
    Drive,
}

/// <summary>One file, folder or drive as the file list shows it. Instances never change after they are created.</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required EntryKind Kind { get; init; }

    /// <summary>The file size in bytes, or the total size for a drive, or 0 for a folder.</summary>
    public long Size { get; init; }

    public DateTime ModifiedUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>The free space in bytes for a drive, otherwise 0.</summary>
    public long FreeBytes { get; init; }

    /// <summary>The volume label for a drive, otherwise empty.</summary>
    public string Label { get; init; } = "";

    public DriveType DriveType { get; init; } = DriveType.Unknown;

    public bool IsContainer => Kind != EntryKind.File;

    /// <summary>The extension with its dot, such as ".txt", or empty for folders, drives and files without one.</summary>
    public string Extension => Kind == EntryKind.File ? Path.GetExtension(Name) : "";

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>True when the entry describes the same item in the same state, so the list can keep its row.</summary>
    public bool SameState(FileEntry other) =>
        Kind == other.Kind && Size == other.Size && ModifiedUtc == other.ModifiedUtc && Attributes == other.Attributes
        && FreeBytes == other.FreeBytes && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override string ToString() => FullPath;
}
