using KanbanTasker.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LocalPreferences preferences = LocalPreferences.Load();
    private readonly WorkspaceStore store;
    private readonly ReminderService reminders = new();
    private WorkspaceDocument? document;
    private Guid? boardId;
    private bool rendering, loaded, closed, working, allowClose, closePromptOpen;
    private bool rootLoaded;
    private volatile bool openingStartupWorkspace = true;
    private readonly DateTime startupTimeUtc = DateTime.UtcNow;
    private TaskData? originalTask;
    private Guid draftColumnId;
    private Guid draftBoardId;
    private readonly List<string> draftTags = [];
    private string? storageError;
    private string? lastErrorMessage;
    private record Choice(Guid Id, string Name, bool IsGitHub = false)
    {
        public Visibility GitHubVisibility => IsGitHub ? Visibility.Visible : Visibility.Collapsed;
    }
    private record ReminderChoice(int? Minutes, string Name);
    private ReminderChoice[] ReminderChoices = [];

    public MainWindow()
    {
        text = new(preferences.Language);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = text.Culture.Name;
        InitializeComponent();
        AppNameText.Text = Distribution.AppDistribution.DisplayName;
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("Window XAML initialized");
#endif
        ApplyLanguage();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "KanbanTasker.ico"));
        InitializeBoardDragging();
        // Overlay light-dismiss must not bypass the explicit save/discard actions.
        TaskPane.PaneClosing += (_, args) => args.Cancel = !closeEditorRequested;
        TaskPane.PaneClosed += (_, _) => closeEditorRequested = false;
        InitializeAppearance();
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("Appearance initialized");
#endif
        boardId = preferences.SelectedBoard;
        store = new(Path.Combine(LocalPreferences.DirectoryPath, "Recovery"), preferences.DeviceId);
        InitializeGitHub();
        store.Changed += Store_Changed;
        BoardMenuButton.IsEnabled = CalendarButton.IsEnabled = false;
        if (preferences.FilePath is not null)
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
            PathText.Text = preferences.FilePath;
            StatusText.Text = T("Loading boards…");
        }
        CenterStartupWindow();
        Root.SizeChanged += (_, _) =>
        {
            TaskPane.DisplayMode = Root.ActualWidth >= 1100 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
            UpdateGroupSelectorLayout();
        };
        Activated += async (_, args) =>
        {
            if (loaded && args.WindowActivationState != WindowActivationState.Deactivated)
            { await store.RefreshAsync(); await RefreshGitHubAsync(true); }
        };
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose || !TaskPane.IsPaneOpen && !working) return;
            args.Cancel = true;
            if (working || closePromptOpen) return;
            closePromptOpen = true;
            try { if (await CanDiscardDraftAsync()) { allowClose = true; Close(); } }
            finally { closePromptOpen = false; }
        };
        Closed += async (_, _) =>
        {
            closed = true; store.Changed -= Store_Changed;
            preferences.SelectedBoard = boardId;
            try { preferences.Save(); } catch (IOException) { }
            await store.DisposeAsync();
        };
    }
    internal void ActivateWhenReady()
    {
        if (rootLoaded && !closed) Activate();
    }
    private void CenterStartupWindow()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var width = Math.Min(1420, work.Width);
        var height = Math.Min(900, work.Height);
        // WorkArea offsets are relative to this display; the overload handles its screen origin.
        // Position once before Activate(), keeping later user moves and resizes untouched.
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width, height), display);
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (rootLoaded) return;
        rootLoaded = true;
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.WindowWasHiddenBeforeLoaded = !AppWindow.IsVisible;
#endif
        // Loaded is raised once the lightweight XAML shell is ready, even while hidden.
        // Showing the native window earlier exposes its unpainted black client area.
        ActivateWhenReady();
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("XAML root loaded; activating main window and opening workspace");
#endif
        _ = Task.Run(() => CleanUpdateCache(startupTimeUtc));
        await RunAsync(() => Task.Run(async () =>
        {
            preferences.Save();
            if (preferences.FilePath is not null) await store.OpenAsync(preferences.FilePath);
        }));
        if (closed) return;
        loaded = true;
        openingStartupWorkspace = false;
        try { await github.InitializeAsync(githubLifetime.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
        githubTimer.Start();
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("Initial workspace opened");
#endif
        Render();
        _ = RefreshGitHubAsync(true);
        if (taskbarPinPending) await ShowTaskbarPinOfferAsync();
#if KANBAN_UI_SMOKE_TEST
        await RunDesktopSmokeTestsAsync();
#endif
    }
    private void Store_Changed(object? sender, EventArgs args)
    {
        // Startup renders the validated file snapshot once, after opening completes.
        if (openingStartupWorkspace) return;
        DispatcherQueue.TryEnqueue(() => { if (!closed) Render(); });
    }
    private void Render()
    {
        document = store.Current;
        rendering = true;
        var boards = RenderGroupPicker();
        if (!boards.Any(x => x.Id == boardId)) boardId = boards.FirstOrDefault()?.Id;
        BoardPicker.ItemsSource = boards;
        BoardPicker.SelectedItem = boards.FirstOrDefault(x => x.Id == boardId);
        if (PresentedGitHubBoard is { } remote) document = GitHubProjection(remote.Project,remote.View);
        var board = document?.Boards.FirstOrDefault(x => x.Id == boardId);
        ToolTipService.SetToolTip(BoardPicker, board?.Get<string>(Fields.Notes) ?? T("Choose a board"));
        BoardMenuButton.IsEnabled = true;
        EditBoardMenuItem.IsEnabled = !IsGitHubBoard && boardId is not null;
        DeleteBoardMenuItem.Text = T(IsGitHubBoard ? "Unlink GitHub board" : "Delete board");
        DeleteBoardMenuItem.IsEnabled = boardId is not null;
        RefreshGitHubMenuItem.IsEnabled = github.Registry.Links.Count > 0;
        ReviewGitHubMenuItem.IsEnabled = github.Operations.Count > 0;
        CalendarButton.IsEnabled = boardId is not null && !IsGitHubBoard;
        WelcomePanel.Visibility = boardId is null ? Visibility.Visible : Visibility.Collapsed;
        WelcomeActions.Visibility = document is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyBoardButton.Visibility = document is not null ? Visibility.Visible : Visibility.Collapsed;
        var emptyGroup = document is not null && preferences.GroupsEnabled && preferences.SelectedGroup is not null;
        WelcomeTitle.Text = document is null ? T("Your boards, in one local file") : emptyGroup ? T("No boards in this group") : T("Ready for your first board");
        WelcomeText.Text = document is null
            ? T("Create a data file or open an existing one. Choose a locally available Nextcloud folder to use the same boards on your other devices.")
            : emptyGroup ? T("Create a board here or choose another group.") : T("Create a board to start organizing your tasks.");
        EmptyBoardButton.Content = emptyGroup ? T("New board") : T("Create your first board");
        PathText.Text = store.FilePath ?? T("Choose a local data file to get started.");
        ToolTipService.SetToolTip(PathText, store.FilePath ?? "");
        StatusText.Text = store.Status.State switch
        {
            StorageState.Saved => T("Saved locally"), StorageState.Pending => T("Saving…"),
            StorageState.Error => T("File needs attention"), _ => T("No data file open")
        };
        ToolTipService.SetToolTip(StatusText, text.TranslateDiagnostic(store.Status.Message) + " " + T("Your sync app manages transfers to other devices."));
        if (store.Status.State == StorageState.Error)
        {
            storageError = text.TranslateDiagnostic(store.Status.Message); ShowError(storageError);
        }
        else if (storageError is not null)
        {
            if (ErrorBar.Message == storageError) ErrorBar.IsOpen = false;
            storageError = null;
        }
        Title = board is null ? Distribution.AppDistribution.DisplayName : $"{board.Get<string>(Fields.Name)} — {Distribution.AppDistribution.DisplayName}";
        RenderBoard();
        if (SelectedGitHubProject is { } githubProject)
        {
            PathText.Text = githubProject.Organization + " / " + githubProject.Title + " · GitHub";
            ToolTipService.SetToolTip(PathText, "https://github.com/orgs/" + githubProject.Organization + "/projects/" + githubProject.Number);
            StatusText.Text=github.Online(githubProject.Id) ? T("GitHub · updated {0:T}",githubProject.FetchedAt.ToLocalTime()) : T("GitHub cached board · read-only");
            var githubError=github.RetryAt > DateTimeOffset.UtcNow ? "GitHub rate limit reached. Editing is paused until requests are allowed again." : github.Error(githubProject.Id);
            ToolTipService.SetToolTip(StatusText,githubError is null ? T("GitHub checks for changes automatically.") : text.TranslateDiagnostic(githubError));
            GitHubSyncNotice.IsOpen=githubError is not null;
            GitHubSyncNotice.Title=T("GitHub refresh failed");
            GitHubSyncNotice.Message=text.TranslateDiagnostic(githubError ?? "")+" "+T("Updates are retried automatically.");
            RetryGitHubButton.Content=T("Refresh GitHub boards");
            RetryGitHubButton.IsEnabled=!(github.RetryAt > DateTimeOffset.UtcNow);
        }
        else GitHubSyncNotice.IsOpen=false;
        GitHubWriteProgress.Visibility=ShowingGitHubPending ? Visibility.Visible : Visibility.Collapsed;
        GitHubWriteProgress.IsActive=ShowingGitHubPending;
        if (ShowingGitHubPending)
        {
            StatusText.Text=T("Waiting for GitHub…");
            ToolTipService.SetToolTip(StatusText,githubPending!.Preview.Title);
        }
        if (TaskPane.IsPaneOpen) RefreshDraftContext();
        rendering = false;
        if (store.Current is { } localWorkspace)
        {
            var warning = reminders.Update(localWorkspace, text);
            if (warning is not null && store.Status.State != StorageState.Error) ShowError(warning);
        }
    }
    private async void BoardPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || BoardPicker.SelectedItem is not Choice choice || choice.Id == boardId) return;
        if (working && (githubEditor || IsGitHubBoard)) { Render(); return; }
        if (await CanDiscardDraftAsync())
        {
            CloseEditor(); boardId = choice.Id; preferences.SelectedBoard = boardId;
            await RunAsync(() => { preferences.Save(); Render(); return Task.CompletedTask; });
        }
        else Render();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (working) return;
        working = true;
        if (githubEditor) ApplyGitHubEditorState();
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Runtime.InteropServices.COMException)
        { ShowError(ex.Message); }
        finally { working = false; if (githubEditor) ApplyGitHubEditorState(); }
    }
    private void ShowError(string message)
    {
        lastErrorMessage = message;
        ErrorBar.Title = T("Please check"); ErrorBar.Message = text.TranslateDiagnostic(message); ErrorBar.IsOpen = true;
    }
    private void ApplyTitleBarTheme()
    {
        if (Root.Background is not Microsoft.UI.Xaml.Media.SolidColorBrush background) return;
        var foreground = (Brush("TextFillColorPrimaryBrush") as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color
            ?? (Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        AppWindow.TitleBar.BackgroundColor = background.Color;
        AppWindow.TitleBar.ForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = background.Color;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.InactiveBackgroundColor = background.Color;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = background.Color;
    }
    private ContentDialog Dialog(string title, object content, string? primary = null)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = primary ?? "", CloseButtonText = T("Cancel"),
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot, Language = text.Culture.Name
        };
        ApplyDialogAppearance(dialog);
        return dialog;
    }
    private async Task<bool> ConfirmAsync(string title, string message, string action)
        => await Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }, action).ShowAsync() == ContentDialogResult.Primary;
    private Task<bool> CanDiscardDraftAsync() => !HasDraftChanges ? Task.FromResult(true)
        : ConfirmAsync(T("Discard task draft?"), T("Your unsaved changes will be discarded."), T("Discard"));
    private async void CreateFile_Click(object sender, RoutedEventArgs e) => await RunAsync(() => SelectFileAsync(true));
    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await RunAsync(() => SelectFileAsync(false));
    private async Task SelectFileAsync(bool create)
    {
        if (!await CanDiscardDraftAsync()) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Windows.Storage.StorageFile? file;
        if (create)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "KanbanTasker.kanban" };
            picker.FileTypeChoices.Add("Kanban Tasker JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            file = await picker.PickSaveFileAsync();
        }
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            file = await picker.PickSingleFileAsync();
        }
        if (file is null) return;
        if (create) await store.CreateAsync(file.Path); else await store.OpenAsync(file.Path);
        CloseEditor(); preferences.FilePath = store.FilePath; preferences.SelectedBoard = null; boardId = null;
        preferences.Save(); Render();
    }
}
