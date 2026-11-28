using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Shell;

namespace NaxExplorer.Controls;

/// <summary>
/// One running copy, move or delete in the window's corner: what it does, the item being worked on, a progress bar and
/// a Cancel button. A clean finish removes it immediately; failures remain available to inspect.
/// </summary>
internal sealed class OperationCard : UserControl
{
    private const int MaxFinishedCards = 3;

    private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Maximum = 1, IsIndeterminate = true };
    private readonly Button _action = new() { Padding = new Thickness(10, 3, 10, 3) };
    private readonly Button _forceDelete = new() { Padding = new Thickness(10, 3, 10, 3), Visibility = Visibility.Collapsed };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _failures = new() { Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly OperationProgress _progress;
    private readonly StackPanel _host;
    private readonly string _operationTitle;
    private Func<Action, Task>? _forceDeleteAction;
    private bool _ended;

    public OperationCard(StackPanel host, string title, OperationProgress progress)
    {
        _host = host;
        _operationTitle = title;
        _progress = progress;
        // Retrying the same operation replaces any earlier finished error card for it instead of stacking duplicates.
        foreach (OperationCard prior in host.Children.OfType<OperationCard>().Where(c => c._ended && string.Equals(c._operationTitle, title, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            host.Children.Remove(prior);
        }
        _title.Text = title;
        _detail.Style = (Style)Application.Current.Resources["SecondaryTextStyle"];
        _detail.Text = Loc.T("OperationPreparing");
        _action.Content = Loc.T("Cancel");
        _action.Click += (_, _) =>
        {
            if (_ended) Close();
            else
            {
                _progress.Cancel();
                _action.IsEnabled = false;
                _detail.Text = Loc.T("OperationCancelling");
            }
        };
        _forceDelete.Content = Loc.T("ForceDeleteAsAdminButton");
        _forceDelete.Click += async (_, _) =>
        {
            if (_forceDeleteAction is not { } run) return;
            _forceDelete.IsEnabled = false;
            bool started = false;
            try
            {
                await run(() =>
                {
                    started = true;
                    Close();
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashLog.Write(ex);
            }
            finally
            {
                if (!started) _forceDelete.IsEnabled = true;
            }
        };
        _actions.Children.Add(_forceDelete);
        _actions.Children.Add(_action);
        var failuresScroll = new ScrollViewer { Content = _failures, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            Child = new StackPanel { Spacing = 6, Children = { _title, _detail, _bar, failuresScroll, _actions } },
        };
        host.Children.Add(this);
    }

    /// <summary>Shows the share done and the current item. Called on the UI thread.</summary>
    public void Update(double share, string? item)
    {
        if (_ended) return;
        if (share > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = share;
        }
        if (!_progress.IsCancelled && item is not null) _detail.Text = item;
    }

    /// <summary>
    /// Ends the card: a clean finish closes it immediately; failures stay listed until the user closes it. A
    /// failure of the whole operation comes as <paramref name="error"/>.
    /// </summary>
    public void Finish(string? error, Func<Action, Task>? forceDeleteAction = null)
    {
        _ended = true;
        IReadOnlyList<OperationFailure> failures = _progress.Failures;
        _bar.IsIndeterminate = false;
        _action.IsEnabled = true;
        _action.Content = Loc.T("Close");
        if (failures.Count == 0 && error is null)
        {
            Close();
            return;
        }
        _forceDeleteAction = forceDeleteAction;
        _forceDelete.Visibility = forceDeleteAction is not null ? Visibility.Visible : Visibility.Collapsed;
        _forceDelete.IsEnabled = forceDeleteAction is not null;
        _bar.Visibility = Visibility.Collapsed;
        _detail.Text = failures.Count == 1
            ? Loc.T("OperationFailedItem")
            : failures.Count > 1
                ? Loc.F("OperationFailedItems", failures.Count)
                : error!;
        _detail.Style = null;
        _detail.TextWrapping = TextWrapping.Wrap;
        foreach (OperationFailure failure in failures.Take(50))
        {
            _failures.Children.Add(new TextBlock { Text = $"{failure.Name}: {failure.Message}", FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        }
        _failures.Visibility = Visibility.Visible;
        // Deduplicate any concurrent finished error card for the same operation and cap older finished cards without evicting this one.
        foreach (OperationCard duplicate in _host.Children.OfType<OperationCard>().Where(c => c._ended && !ReferenceEquals(c, this) && string.Equals(c._operationTitle, _operationTitle, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _host.Children.Remove(duplicate);
        }
        List<OperationCard> priorFinished = _host.Children.OfType<OperationCard>().Where(c => c._ended && !ReferenceEquals(c, this)).ToList();
        for (int i = 0; i <= priorFinished.Count - MaxFinishedCards; i++) _host.Children.Remove(priorFinished[i]);
    }

    private void Close() => _host.Children.Remove(this);
}
