using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxExplorer.Core;

namespace NaxExplorer.Controls;

/// <summary>The app's own questions before a copy, move or delete, in place of Windows' conflict and delete windows.</summary>
internal static class FileOperationDialogs
{
    /// <summary>
    /// Asks what to do with items whose names already exist in the target. Returns null when the user cancels, so
    /// nothing is copied or moved at all.
    /// </summary>
    public static async Task<ConflictChoice?> AskConflictAsync(XamlRoot root, ElementTheme theme, TransferConflicts conflicts, string targetName, bool move)
    {
        ConflictChoice? choice = null;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = Loc.F("ConflictTitle", conflicts.Items.Count, targetName),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.None,
        };
        var names = new StackPanel { Spacing = 2 };
        foreach (string item in conflicts.Items.Take(6)) names.Children.Add(new TextBlock { Text = Path.GetFileName(item.TrimEnd('\\')), TextTrimming = TextTrimming.CharacterEllipsis });
        if (conflicts.Items.Count > 6) names.Children.Add(new TextBlock { Text = Loc.F("ConflictMore", conflicts.Items.Count - 6), Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        var options = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        string replaceDetail = conflicts.FilesReplaced == 0
            ? Loc.T("ConflictReplaceDetailNone")
            : Loc.F(conflicts.Capped ? "ConflictReplaceDetailAtLeast" : "ConflictReplaceDetail", conflicts.FilesReplaced);
        options.Children.Add(Option("\uE72C", Loc.T(move ? "ConflictReplaceMove" : "ConflictReplace"), replaceDetail, () => choice = ConflictChoice.Replace));
        options.Children.Add(Option("\uE8BB", Loc.T("ConflictSkip"), Loc.T("ConflictSkipDetail"), () => choice = ConflictChoice.Skip));
        options.Children.Add(Option("\uE8C8", Loc.T("ConflictKeepBoth"), Loc.T("ConflictKeepBothDetail"), () => choice = ConflictChoice.KeepBoth));
        dialog.Content = new StackPanel { MinWidth = 420, Children = { names, options } };

        Button Option(string glyph, string title, string detail, Action pick)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) =>
            {
                pick();
                dialog.Hide();
            };
            return button;
        }

        await dialog.ShowAsync();
        return choice;
    }

    /// <summary>Asks before items are deleted for good. Returns true only when the user agreed.</summary>
    public static async Task<bool> ConfirmPermanentDeleteAsync(XamlRoot root, ElementTheme theme, IReadOnlyList<string> paths, bool becauseNoRecycleBin)
    {
        string what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : Loc.F("ItemCount", paths.Count);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = Loc.T("DeleteForGoodTitle"),
            Content = new TextBlock { Text = Loc.F(becauseNoRecycleBin ? "DeleteNoRecycleBin" : "DeleteForGoodText", what), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("DeleteForGoodButton"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>True when the drive of <paramref name="path"/> has a Recycle Bin, so a delete there can be undone.</summary>
    public static bool HasRecycleBin(string path) => RecycleBins.HasRecycleBin(path);
}
