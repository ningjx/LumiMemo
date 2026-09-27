using LumiMemo.Core.Models;
using LumiMemo.WinUI.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT;

namespace LumiMemo.WinUI;

/// <summary>Searchable entry point for opening and creating note windows.</summary>
public sealed partial class ManagerWindow : Window
{
    private readonly NoteWindowManager _windows;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _allowClose;

    public ManagerWindow(NoteWindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        InitializeComponent();
        _windows = windows;
        _windows.NotesChanged += OnNotesChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ManagerTitleBar);
        AppWindow.Resize(new SizeInt32(520, 620));
        AppWindow.Closing += OnWindowClosing;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = false;
        }

        EnablePersistentAcrylic();
        RefreshNotes();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    public void HideWindow() => AppWindow.Hide();

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public async Task ShowAboutAsync()
    {
        ShowWindow();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "关于 LumiMemo",
            Content = "鹿米便笺 WinUI 迁移版\nMarkdown 数据保存在本地笔记文件夹中。",
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    private void EnablePersistentAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
            return;
        }

        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Light
        };
        _acrylicController = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
            TintOpacity = 0.34f,
            LuminosityOpacity = 0.58f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248)
        };
        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private async void OnNewNoteClick(object sender, RoutedEventArgs e) =>
        await _windows.CreateNoteAsync();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideWindow();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RefreshNotes();

    private void OnNoteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Note note)
        {
            _windows.OpenNote(note.Id);
        }
    }

    private void OnNotesChanged(object? sender, EventArgs e) => RefreshNotes();

    private void RefreshNotes()
    {
        string query = SearchBox.Text.Trim();
        NotesList.ItemsSource = _windows.Notes
            .Where(note => query.Length == 0
                || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || note.Content.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(note => note.UpdatedAt)
            .ToList();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HideWindow();
            return;
        }

        _windows.NotesChanged -= OnNotesChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
    }
}
