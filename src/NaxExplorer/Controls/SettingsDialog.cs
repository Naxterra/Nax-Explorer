using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Core;
using NaxExplorer.Search;
using NaxExplorer.Services;
using Microsoft.Windows.Storage.Pickers;

namespace NaxExplorer.Controls;

/// <summary>
/// Settings: language, theme and list options, and AI search with its model download, indexed folders and pause rules.
/// Changes are written to the settings object at once; the window applies them when the dialog closes.
/// </summary>
internal sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings _settings;
    private readonly string _initialLanguage;
    private readonly StackPanel _folders = new() { Spacing = 4 };
    private readonly TextBlock _aiState = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ProgressBar _download = new() { Visibility = Visibility.Collapsed, Maximum = 1 };
    private readonly Button _downloadButton = new();
    private readonly ToggleSwitch _aiSwitch = new();
    private readonly StackPanel _aiDetails = new() { Spacing = 10 };
    private CancellationTokenSource? _downloadCts;

    public bool LanguageChanged => _settings.Language != _initialLanguage;

    public SettingsDialog(AppSettings settings, bool aiSection)
    {
        _settings = settings;
        _initialLanguage = settings.Language;
        Title = Loc.T("Settings");
        CloseButtonText = Loc.T("Close");
        DefaultButton = ContentDialogButton.Close;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 720.0;

        var general = new StackPanel { Spacing = 12 };
        general.Children.Add(Header("SectionGeneral"));
        general.Children.Add(Choice("Language", ["LanguageSystem", "LanguageEnglish", "LanguageGerman"], settings.Language switch { "en-US" => 1, "de-DE" => 2, _ => 0 },
            index => settings.Language = index switch { 1 => "en-US", 2 => "de-DE", _ => "" }));
        general.Children.Add(Choice("Theme", ["ThemeSystem", "ThemeLight", "ThemeDark"], settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 },
            index => settings.Theme = index switch { 1 => "light", 2 => "dark", _ => "system" }));
        general.Children.Add(Switch("ShowHidden", settings.ShowHidden, on => settings.ShowHidden = on));
        general.Children.Add(Switch("ShowExtensions", settings.ShowExtensions, on => settings.ShowExtensions = on));
        general.Children.Add(Switch("ColorfulNames", settings.ColorfulNames, on => settings.ColorfulNames = on));
        general.Children.Add(Switch("OpenPicturesInViewer", settings.OpenImagesInViewer, on => settings.OpenImagesInViewer = on));

        var ai = new StackPanel { Spacing = 12 };
        var aiHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        aiHeader.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)Application.Current.Resources["NaxGradientBrush"],
            Child = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        });
        aiHeader.Children.Add(Header("SectionAi"));
        ai.Children.Add(aiHeader);
        ai.Children.Add(new TextBlock { Text = Loc.T("AiExplain"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        _aiSwitch.Header = Loc.T("AiSwitch");
        _aiSwitch.IsOn = settings.AiSearch;
        _aiSwitch.Toggled += AiSwitch_Toggled;
        ai.Children.Add(_aiSwitch);
        _downloadButton.Content = Loc.F("DownloadModel", SizeFormat.Format(EmbeddingModel.TotalBytes));
        _downloadButton.Click += async (_, _) => await DownloadAsync();
        _downloadButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ai.Children.Add(_aiState);
        ai.Children.Add(_download);
        ai.Children.Add(_downloadButton);

        _aiDetails.Children.Add(new TextBlock { Text = Loc.T("IndexedFolders"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _aiDetails.Children.Add(_folders);
        var addFolder = new Button { Content = Loc.T("AddFolder") };
        addFolder.Click += async (_, _) => await AddFolderAsync();
        _aiDetails.Children.Add(addFolder);
        _aiDetails.Children.Add(Switch("UseGpu", settings.UseGpu, on =>
        {
            settings.UseGpu = on;
            App.Instance!.Search.Apply(settings, restart: true);
        }));
        _aiDetails.Children.Add(Switch("PauseFullScreen", settings.PauseIndexingInFullScreen, on =>
        {
            settings.PauseIndexingInFullScreen = on;
            App.Instance!.Search.Apply(settings, restart: false);
        }));
        _aiDetails.Children.Add(Switch("PauseBattery", settings.PauseIndexingOnBattery, on =>
        {
            settings.PauseIndexingOnBattery = on;
            App.Instance!.Search.Apply(settings, restart: false);
        }));
        var maintenance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rescan = new Button { Content = Loc.T("Rescan") };
        rescan.Click += (_, _) => App.Instance!.Search.Rescan();
        var deleteIndex = new Button { Content = Loc.T("DeleteIndex") };
        deleteIndex.Click += (_, _) => DeleteIndex(model: false);
        var deleteAll = new Button { Content = Loc.T("DeleteIndexAndModel") };
        deleteAll.Click += (_, _) => DeleteIndex(model: true);
        maintenance.Children.Add(rescan);
        maintenance.Children.Add(deleteIndex);
        maintenance.Children.Add(deleteAll);
        _aiDetails.Children.Add(maintenance);
        _aiDetails.Children.Add(new TextBlock
        {
            Text = Loc.F("AiDataLocation", DataFolders.Root),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            IsTextSelectionEnabled = true,
        });
        ai.Children.Add(_aiDetails);

        var content = new StackPanel { Spacing = 24, Padding = new Thickness(0, 0, 16, 0) };
        if (aiSection)
        {
            content.Children.Add(ai);
            content.Children.Add(general);
        }
        else
        {
            content.Children.Add(general);
            content.Children.Add(ai);
        }
        content.Children.Add(new TextBlock
        {
            Text = $"Nax Explorer {typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)}  ·  © 2026 Naxterra  ·  {Loc.T("LicenseNote")}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        Content = new ScrollViewer { Content = content, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, _) => _downloadCts?.Cancel();
        RefreshAi();
    }

    private static TextBlock Header(string key) => new() { Text = Loc.T(key), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static ToggleSwitch Switch(string key, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Header = Loc.T(key), IsOn = value };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static ComboBox Choice(string key, string[] options, int selected, Action<int> changed)
    {
        var box = new ComboBox { Header = Loc.T(key), MinWidth = 240 };
        foreach (string option in options) box.Items.Add(Loc.T(option));
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) => changed(box.SelectedIndex);
        return box;
    }

    private void RefreshAi()
    {
        bool installed = EmbeddingModel.IsInstalled();
        _downloadButton.Visibility = _settings.AiSearch && !installed && _downloadCts is null ? Visibility.Visible : Visibility.Collapsed;
        _aiDetails.Visibility = _settings.AiSearch ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.AiSearch) _aiState.Text = Loc.T("AiOffState");
        else if (!installed) _aiState.Text = Loc.F("AiModelMissing", SizeFormat.Format(EmbeddingModel.TotalBytes));
        else _aiState.Text = Loc.F("AiModelReady", App.Instance!.Search.Device ?? Loc.T("DeviceNotLoaded"));
        _folders.Children.Clear();
        foreach (string folder in _settings.IndexedFolders.ToList())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(6) };
            ToolTipService.SetToolTip(remove, Loc.T("RemoveFolder"));
            remove.Click += (_, _) =>
            {
                _settings.IndexedFolders.Remove(folder);
                App.Instance!.Search.Apply(_settings, restart: false);
                RefreshAi();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _folders.Children.Add(row);
        }
    }

    private async void AiSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.AiSearch = _aiSwitch.IsOn;
        if (_settings.AiSearch && _settings.IndexedFolders.Count == 0) _settings.IndexedFolders = AppSettings.DefaultIndexedFolders();
        App.Instance!.SaveSettings();
        if (!_settings.AiSearch)
        {
            App.Instance.Search.Stop();
            RefreshAi();
            return;
        }
        RefreshAi();
        if (EmbeddingModel.IsInstalled()) App.Instance.Search.Start(_settings);
        else await DownloadAsync();
    }

    /// <summary>Downloads the model with progress in the dialog, then starts AI search.</summary>
    private async Task DownloadAsync()
    {
        if (_downloadCts is not null) return;
        _downloadCts = new CancellationTokenSource();
        _download.Visibility = Visibility.Visible;
        _download.Value = 0;
        _downloadButton.Visibility = Visibility.Collapsed;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            _download.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            _aiState.Text = Loc.F("Downloading", SizeFormat.Format(p.Done), SizeFormat.Format(p.Total));
        });
        try
        {
            await EmbeddingModel.DownloadAsync(progress, _downloadCts.Token);
            App.Instance!.Search.Start(_settings);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _aiState.Text = Loc.F("DownloadFailed", ex.Message);
            _downloadCts = null;
            _download.Visibility = Visibility.Collapsed;
            _downloadButton.Visibility = Visibility.Visible;
            return;
        }
        _downloadCts = null;
        _download.Visibility = Visibility.Collapsed;
        RefreshAi();
    }

    private async Task AddFolderAsync()
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var result = await picker.PickSingleFolderAsync();
        if (result?.Path is not { Length: > 0 } folder) return;
        if (_settings.IndexedFolders.Any(f => PathTools.IsSameOrInside(folder, f))) return;
        _settings.IndexedFolders.RemoveAll(f => PathTools.IsSameOrInside(f, folder));
        _settings.IndexedFolders.Add(folder);
        App.Instance!.SaveSettings();
        App.Instance.Search.Apply(_settings, restart: false);
        RefreshAi();
    }

    private void DeleteIndex(bool model)
    {
        App.Instance!.Search.Stop();
        try
        {
            SearchService.DeleteData(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _aiState.Text = ex.Message;
            return;
        }
        if (model)
        {
            _settings.AiSearch = false;
            _aiSwitch.IsOn = false;
            App.Instance.SaveSettings();
        }
        else if (_settings.AiSearch)
        {
            App.Instance.Search.Start(_settings);
        }
        RefreshAi();
    }
}
