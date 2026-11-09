using Windows.System;

namespace NaxExplorer.Core;

public enum ViewerCommand { None, Close, Previous, Next, First, Last, ZoomIn, ZoomOut, Fit, ActualSize, Rotate, FullScreen, Delete, OpenExternal }

/// <summary>Viewer shortcuts take precedence over scrolling and covered file lists.</summary>
public static class ViewerKeyboard
{
    public static ViewerCommand Command(VirtualKey key, bool visible, bool buttonFocused = false, bool control = false, bool alt = false)
    {
        if (!visible || control || alt) return ViewerCommand.None;
        // Space and Enter activate a focused toolbar button in the usual way.
        if (buttonFocused && key is VirtualKey.Space or VirtualKey.Enter) return ViewerCommand.None;
        return key switch
        {
            VirtualKey.Escape => ViewerCommand.Close,
            VirtualKey.Left or VirtualKey.PageUp or VirtualKey.Back => ViewerCommand.Previous,
            VirtualKey.Right or VirtualKey.PageDown or VirtualKey.Space => ViewerCommand.Next,
            VirtualKey.Home => ViewerCommand.First,
            VirtualKey.End => ViewerCommand.Last,
            VirtualKey.Add or (VirtualKey)187 => ViewerCommand.ZoomIn,
            VirtualKey.Subtract or (VirtualKey)189 => ViewerCommand.ZoomOut,
            VirtualKey.Number0 or VirtualKey.NumberPad0 => ViewerCommand.Fit,
            VirtualKey.Number1 or VirtualKey.NumberPad1 => ViewerCommand.ActualSize,
            VirtualKey.R => ViewerCommand.Rotate,
            VirtualKey.F11 or VirtualKey.F => ViewerCommand.FullScreen,
            VirtualKey.Delete => ViewerCommand.Delete,
            VirtualKey.Enter => ViewerCommand.OpenExternal,
            _ => ViewerCommand.None,
        };
    }
}
