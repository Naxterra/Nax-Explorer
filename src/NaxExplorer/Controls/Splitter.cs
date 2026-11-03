using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NaxExplorer.Controls;

/// <summary>A thin vertical bar that resizes the columns next to it. It reports how far it was dragged.</summary>
public sealed partial class Splitter : Grid
{
    private double _start;
    private bool _dragging;

    public event EventHandler<double>? Dragged;

    public Splitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Children.Add(new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24),
            Opacity = 0,
            Background = (Brush)Application.Current.Resources["NaxGradientBrush"],
        });
        PointerEntered += (_, _) => Line.Opacity = 0.8;
        PointerExited += (_, _) =>
        {
            if (!_dragging) Line.Opacity = 0;
        };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) =>
        {
            _dragging = false;
            Line.Opacity = 0;
        };
    }

    private Border Line => (Border)Children[0];

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        _start = e.GetCurrentPoint(null).Position.X;
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double x = e.GetCurrentPoint(null).Position.X;
        double delta = x - _start;
        if (Math.Abs(delta) < 1) return;
        _start = x;
        Dragged?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        Line.Opacity = 0;
        e.Handled = true;
    }
}
