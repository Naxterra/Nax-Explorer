using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace NaxExplorer.Models;

/// <summary>Data payload attached to each WinUI TreeViewNode in the sidebar folder tree.</summary>
public sealed class FolderTreeNode : INotifyPropertyChanged
{
    private string _label;
    private string _glyph;
    private Brush? _brush;
    private string? _detail;

    public FolderTreeNode(string label, string location, string glyph, Brush? brush, bool isShellPlace = false, bool isRoot = false, string? detail = null)
    {
        _label = label;
        Location = location;
        _glyph = glyph;
        _brush = brush;
        IsShellPlace = isShellPlace;
        IsRoot = isRoot;
        _detail = string.IsNullOrEmpty(detail) ? null : detail;
    }

    public string Location { get; }

    public bool IsShellPlace { get; }

    public bool IsRoot { get; }

    public bool ChildrenLoaded { get; set; }

    public int RefreshVersion { get; set; }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public string Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    public Brush? Brush
    {
        get => _brush;
        set
        {
            if (ReferenceEquals(_brush, value)) return;
            _brush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        }
    }

    public string? Detail
    {
        get => _detail;
        set
        {
            string? normalized = string.IsNullOrEmpty(value) ? null : value;
            if (_detail == normalized) return;
            _detail = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
