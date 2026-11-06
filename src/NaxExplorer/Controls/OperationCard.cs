using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Shell;

namespace NaxExplorer.Controls;

/// <summary>
/// One running copy, move or delete in the window's corner: what it does, the item being worked on, a progress bar and
/// a Cancel button. When it ends it says so briefly and disappears, or stays open with the items that failed.
/// </summary>
internal sealed class OperationCard : UserControl
{
    private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Maximum = 1, IsIndeterminate = true };
    private readonly Button _action = new() { Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _failures = new() { Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly OperationProgress _progress;
    private readonly StackPanel _host;
    private bool _ended;

    public OperationCard(StackPanel host, string title, OperationProgress progress)
    {
        _host = host;
        _progress = progress;
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
        var failuresScroll = new ScrollViewer { Content = _failures, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            Child = new StackPanel { Spacing = 6, Children = { _title, _detail, _bar, failuresScroll, _action } },
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
    /// Ends the card: a clean finish closes it after a moment; failures stay listed until the user closes it. A
    /// failure of the whole operation comes as <paramref name="error"/>.
    /// </summary>
    public void Finish(string? error)
    {
        _ended = true;
        IReadOnlyList<OperationFailure> failures = _progress.Failures;
        _bar.IsIndeterminate = false;
        _action.IsEnabled = true;
        _action.Content = Loc.T("Close");
        if (failures.Count == 0 && error is null)
        {
            _bar.Value = 1;
            _detail.Text = Loc.T(_progress.IsCancelled ? "OperationCancelled" : "OperationDone");
            StartClose(DispatcherQueue.CreateTimer());
            return;
        }
        _bar.Visibility = Visibility.Collapsed;
        _detail.Text = failures.Count > 0 ? Loc.F("OperationFailedItems", failures.Count) : error!;
        _detail.Style = null;
        _detail.TextWrapping = TextWrapping.Wrap;
        foreach (OperationFailure failure in failures.Take(50))
        {
            _failures.Children.Add(new TextBlock { Text = $"{failure.Name}: {failure.Message}", FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        }
        _failures.Visibility = Visibility.Visible;
    }

    private void StartClose(Microsoft.UI.Dispatching.DispatcherQueueTimer timer)
    {
        timer.Interval = TimeSpan.FromSeconds(2.5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Close();
        timer.Start();
    }

    private void Close() => _host.Children.Remove(this);
}
