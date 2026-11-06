using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaxExplorer.Core;
using NaxExplorer.Services;
using NaxExplorer.Shell;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace NaxExplorer.Controls;

/// <summary>
/// The built-in picture viewer over the whole window. It steps through the pictures of the folder with the arrow keys,
/// zooms with the mouse wheel around the pointer, pans by dragging, turns pictures for viewing without changing the
/// file, and has a full-screen mode. The next picture is decoded ahead, so stepping forward is instant.
/// </summary>
public sealed partial class ImageViewer : UserControl
{
    private const int DecodeLimit = 8192;

    private MainWindow _owner = null!;
    private FilePane? _source;
    private List<string> _pictures = [];
    private int _index;
    private int _turns;
    private int _version;
    private (string Path, int Turns, Task<LoadedImage> Image)? _ahead;
    private CancellationTokenSource? _cts;
    private bool _fit = true;
    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panOffsetX;
    private double _panOffsetY;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _imageScale = 1;

    public ImageViewer()
    {
        InitializeComponent();
        Canvas.ManipulationMode = ManipulationModes.None;
    }

    internal void Attach(MainWindow owner) => _owner = owner;

    internal void Open(IReadOnlyList<string> pictures, int index, FilePane source)
    {
        _pictures = [.. pictures];
        _index = Math.Clamp(index, 0, _pictures.Count - 1);
        _source = source;
        Visibility = Visibility.Visible;
        Root.Focus(FocusState.Programmatic);
        _ = ShowCurrentAsync();
    }

    internal void Close()
    {
        _cts?.Cancel();
        _version++;
        _ahead = null;
        if (_owner.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) _owner.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        FullScreenGlyph.Glyph = "";
        Picture.Source = null;
        Visibility = Visibility.Collapsed;
        if (_source is not null && _pictures.Count > 0)
        {
            _source.Reveal(_pictures[_index]);
            _source.FocusList();
        }
    }

    private async Task ShowCurrentAsync()
    {
        if (_pictures.Count == 0)
        {
            Close();
            return;
        }
        int version = ++_version;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        string path = _pictures[_index];
        NameText.Text = Path.GetFileName(path);
        InfoText.Text = $"{_index + 1} / {_pictures.Count}";
        PreviousButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _index < _pictures.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        if (Core.CloudFiles.IsOnlineOnly(path))
        {
            // The viewer never downloads a picture that lives only in the cloud; the user does that with "keep on this device".
            Picture.Source = null;
            ErrorText.Text = Loc.T("OnlineOnlyNote");
            ErrorText.Visibility = Visibility.Visible;
            Busy.IsActive = false;
            LoadAhead();
            return;
        }
        Busy.IsActive = true;
        LoadedImage image;
        try
        {
            Task<LoadedImage> load = _ahead is { } ahead && ahead.Path == path && ahead.Turns == _turns ? ahead.Image : ImageLoader.LoadAsync(path, DecodeLimit, cts.Token, _turns);
            _ahead = null;
            image = await load;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version != _version) return;
            Busy.IsActive = false;
            Picture.Source = null;
            ErrorText.Text = ImageLoader.ErrorText(path, ex);
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (version != _version) return;
        Busy.IsActive = false;
        _pixelWidth = image.Width;
        _pixelHeight = image.Height;
        ApplyPixelSize();
        Picture.Source = image.Source;
        long size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        InfoText.Text = $"{_index + 1} / {_pictures.Count}   ·   {image.OriginalWidth} × {image.OriginalHeight}   ·   {SizeFormat.Format(size)}";
        _fit = true;
        // The new picture size takes effect in the next layout pass; fitting before it used the old size, which left a
        // turned picture unfitted and off centre.
        Stage.UpdateLayout();
        FitToWindow();
        DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version == _version && _fit) FitToWindow();
        });
        LoadAhead();
    }

    /// <summary>Starts decoding the next picture while this one is looked at.</summary>
    private void LoadAhead()
    {
        if (_index + 1 >= _pictures.Count) return;
        string next = _pictures[_index + 1];
        // A picture that lives only in the cloud is never read by the viewer, ahead or when shown.
        if (Core.CloudFiles.IsOnlineOnly(next)) return;
        try
        {
            _ahead = (next, _turns, ImageLoader.LoadAsync(next, DecodeLimit, CancellationToken.None, _turns));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ahead = null;
        }
    }

    private void Step(int delta)
    {
        int target = Math.Clamp(_index + delta, 0, _pictures.Count - 1);
        if (target == _index) return;
        _index = target;
        _turns = 0;
        _ = ShowCurrentAsync();
    }

    // ---------------------------------------------------------------- zoom and pan

    private void ApplyPixelSize()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        _imageScale = scale;
        // At 100 % one picture pixel is one physical screen pixel, including after a DPI transition.
        Picture.Width = Core.DpiMetrics.LogicalPixels(_pixelWidth, scale);
        Picture.Height = Core.DpiMetrics.LogicalPixels(_pixelHeight, scale);
    }

    internal void OnRasterizationScaleChanged()
    {
        if (Visibility != Visibility.Visible || Picture.Source is null) return;
        double oldScale = _imageScale, newScale = XamlRoot?.RasterizationScale ?? 1;
        double x = Core.DpiMetrics.RescaleOffset(Scroller.HorizontalOffset, oldScale, newScale);
        double y = Core.DpiMetrics.RescaleOffset(Scroller.VerticalOffset, oldScale, newScale);
        double zoom = Scroller.ZoomFactor;
        ApplyPixelSize();
        Stage.UpdateLayout();
        if (_fit) FitToWindow();
        else
        {
            UpdateCanvas(zoom);
            Scroller.ChangeView(x, y, (float)zoom, disableAnimation: true);
        }
    }

    /// <summary>
    /// True once a picture is shown and the viewer has a size. Before that the picture's width is NaN, and zoom values
    /// computed from it would be NaN too, which ScrollViewer rejects with an exception.
    /// </summary>
    private bool HasLayout =>
        double.IsFinite(Picture.Width) && Picture.Width > 0 && double.IsFinite(Picture.Height) && Picture.Height > 0
        && Scroller.ViewportWidth > 0 && Scroller.ViewportHeight > 0;

    private double FitZoom()
    {
        if (!HasLayout) return 1;
        double zoom = Math.Min(Scroller.ViewportWidth / Picture.Width, Scroller.ViewportHeight / Picture.Height);
        return Math.Min(1, zoom);
    }

    private void FitToWindow()
    {
        _fit = true;
        SetZoom(FitZoom(), null);
    }

    /// <summary>Zooms, keeping the point under <paramref name="anchor"/> (in viewport coordinates) in place.</summary>
    private void SetZoom(double zoom, Windows.Foundation.Point? anchor)
    {
        if (!HasLayout || !double.IsFinite(zoom) || zoom <= 0) return;
        zoom = Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        double oldZoom = Scroller.ZoomFactor;
        double viewWidth = Scroller.ViewportWidth, viewHeight = Scroller.ViewportHeight;
        UpdateCanvas(zoom);
        var point = anchor ?? new Windows.Foundation.Point(viewWidth / 2, viewHeight / 2);
        double contentX = (Scroller.HorizontalOffset + point.X) / oldZoom;
        double contentY = (Scroller.VerticalOffset + point.Y) / oldZoom;
        double offsetX = Math.Max(0, contentX * zoom - point.X);
        double offsetY = Math.Max(0, contentY * zoom - point.Y);
        if (_fit)
        {
            offsetX = 0;
            offsetY = 0;
        }
        Scroller.ChangeView(offsetX, offsetY, (float)zoom, disableAnimation: true);
        ZoomText.Text = $"{Math.Round(zoom * 100)} %";
    }

    /// <summary>Keeps the canvas at least as large as the window at this zoom, so the picture can sit in its middle.</summary>
    private void UpdateCanvas(double zoom)
    {
        if (!HasLayout || !double.IsFinite(zoom) || zoom <= 0) return;
        Canvas.Width = Math.Max(Scroller.ViewportWidth / zoom, Picture.Width);
        Canvas.Height = Math.Max(Scroller.ViewportHeight / zoom, Picture.Height);
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        UpdateCanvas(Scroller.ZoomFactor);
        ZoomText.Text = $"{Math.Round(Scroller.ZoomFactor * 100)} %";
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;
        DispatcherQueue.SafeEnqueue(() =>
        {
            if (_fit) FitToWindow();
            else UpdateCanvas(Scroller.ZoomFactor);
        });
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroller);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        _fit = false;
        SetZoom(Scroller.ZoomFactor * (delta > 0 ? 1.2 : 1 / 1.2), point.Position);
        e.Handled = true;
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroller);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed) return;
        _panning = Canvas.CapturePointer(e.Pointer);
        _panStart = point.Position;
        _panOffsetX = Scroller.HorizontalOffset;
        _panOffsetY = Scroller.VerticalOffset;
        ProtectedCursorHelper.Set(Canvas, InputSystemCursorShape.SizeAll);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var position = e.GetCurrentPoint(Scroller).Position;
        Scroller.ChangeView(_panOffsetX - (position.X - _panStart.X), _panOffsetY - (position.Y - _panStart.Y), null, disableAnimation: true);
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Scroller).Properties;
        if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Released) Step(-1);
        else if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Released) Step(1);
        if (!_panning) return;
        _panning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
        ProtectedCursorHelper.Set(Canvas, InputSystemCursorShape.Arrow);
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_fit && FitZoom() < 1)
        {
            _fit = false;
            SetZoom(1, e.GetPosition(Scroller));
        }
        else
        {
            FitToWindow();
        }
    }

    // ---------------------------------------------------------------- commands

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        SetZoom(Scroller.ZoomFactor * 1.25, null);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        SetZoom(Scroller.ZoomFactor / 1.25, null);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        SetZoom(1, null);
    }

    private void Rotate(int turns)
    {
        _turns = (_turns + turns + 4) % 4;
        _ahead = null;
        _ = ShowCurrentAsync();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-1);

    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);

    private void ToggleFullScreen()
    {
        bool full = _owner.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        _owner.AppWindow.SetPresenter(full ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
        FullScreenGlyph.Glyph = full ? "" : "";
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_pictures.Count > 0) ShellActions.Open(_pictures[_index], _owner.Hwnd);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteCurrentAsync(permanently: Native.IsKeyDown(0x10));

    /// <summary>
    /// Moves the picture to the Recycle Bin, or deletes it for good after the app asked, and shows the next one. A
    /// failure shows on the app's progress card, and the picture then stays in the viewer.
    /// </summary>
    private async Task DeleteCurrentAsync(bool permanently)
    {
        if (_pictures.Count == 0) return;
        string path = _pictures[_index];
        if (!await _owner.DeleteAsync(new[] { path }, permanently)) return;
        if (File.Exists(path)) return;
        _pictures.RemoveAt(_index);
        _ahead = null;
        if (_pictures.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Min(_index, _pictures.Count - 1);
        await ShowCurrentAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Native.IsKeyDown(0x10);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (_owner.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullScreen();
                else Close();
                break;
            case VirtualKey.Left:
            case VirtualKey.PageUp:
            case VirtualKey.Back:
                Step(-1);
                break;
            case VirtualKey.Right:
            case VirtualKey.PageDown:
            case VirtualKey.Space:
                Step(1);
                break;
            case VirtualKey.Home:
                Step(-_index);
                break;
            case VirtualKey.End:
                Step(_pictures.Count - 1 - _index);
                break;
            case VirtualKey.Add:
            case (VirtualKey)187:
                ZoomIn_Click(this, e);
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)189:
                ZoomOut_Click(this, e);
                break;
            case VirtualKey.Number0:
            case VirtualKey.NumberPad0:
                FitToWindow();
                break;
            case VirtualKey.Number1:
            case VirtualKey.NumberPad1:
                Actual_Click(this, e);
                break;
            case VirtualKey.R:
                Rotate(shift ? -1 : 1);
                break;
            case VirtualKey.F11:
            case VirtualKey.F:
                ToggleFullScreen();
                break;
            case VirtualKey.Delete:
                await DeleteCurrentAsync(shift);
                break;
            case VirtualKey.Enter:
                OpenExternal_Click(this, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}

/// <summary>Sets the cursor of an element, which UIElement only allows from inside the element's own class.</summary>
internal static class ProtectedCursorHelper
{
    private static readonly System.Reflection.PropertyInfo? Property =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public static void Set(UIElement element, InputSystemCursorShape shape)
    {
        try
        {
            Property?.SetValue(element, InputSystemCursor.Create(shape));
        }
        catch (System.Reflection.TargetInvocationException)
        {
        }
    }
}
