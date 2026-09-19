using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private async void NewBoard_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (document is null) await SelectFileAsync(true);
        if (document is not null && await CanDiscardDraftAsync()) { CloseEditor(); await BoardDialogAsync(null); }
    });
    private async void EditBoard_Click(object sender, RoutedEventArgs e) => await RunAsync(() => boardId is null ? Task.CompletedTask : BoardDialogAsync(boardId));
    private async void DeleteBoard_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (boardId is not { } id || document is null) return;
        var name = document.Boards.Single(x => x.Id == id).Get<string>(Fields.Name);
        if (await ConfirmAsync(T("Delete board?"), T("Delete “{0}”, all its columns and all its tasks?", name), T("Delete")))
        {
            await store.CommitAsync(editor => editor.DeleteBoard(id)); CloseEditor(); Render();
        }
    });
    private async Task BoardDialogAsync(Guid? id)
    {
        var board = document?.Boards.FirstOrDefault(x => x.Id == id);
        var originalName = board?.Get<string>(Fields.Name) ?? "";
        var originalNotes = board?.Get<string>(Fields.Notes) ?? "";
        var name = new TextBox { Header = T("Board name"), Text = originalName, MinWidth = 320 };
        var notes = new TextBox { Header = T("Notes"), Text = originalNotes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
        var fields = new StackPanel { Spacing = 14 }; fields.Children.Add(name); fields.Children.Add(notes); fields.Children.Add(error);
        var dialog = Dialog(id is null ? T("New board") : T("Edit board"), fields, T("Save"));
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await store.CommitAsync(editor =>
                {
                    if (id is null) boardId = editor.CreateBoard(name.Text, notes.Text);
                    else editor.EditBoard(id.Value, name.Text, notes.Text, originalName, originalNotes);
                });
                Render();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { args.Cancel = true; error.Text = text.TranslateDiagnostic(ex.Message); }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
    private async Task ColumnDialogAsync(Guid? id)
    {
        if (document is null || boardId is null) return;
        var parent = boardId.Value;
        var column = document.Columns.FirstOrDefault(x => x.Id == id);
        var originalName = column?.Get<string>(Fields.Name) ?? "";
        var originalLimit = column?.Get<int>(Fields.Limit) ?? 10;
        var name = new TextBox { Header = T("Column name"), Text = originalName, MinWidth = 320 };
        var limit = new NumberBox { Name = "ColumnLimit", Header = T("Task limit (0 = no limit)"), Value = originalLimit, Minimum = 0, Maximum = int.MaxValue,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
        var fields = new StackPanel { Spacing = 14 }; fields.Children.Add(name); fields.Children.Add(limit); fields.Children.Add(error);
        var dialog = Dialog(id is null ? T("New column") : T("Edit column"), fields, T("Save"));
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (double.IsNaN(limit.Value) || limit.Value != Math.Truncate(limit.Value)) throw new ArgumentException("Enter a whole number for the task limit.");
                await store.CommitAsync(editor =>
                {
                    if (id is null) editor.CreateColumn(parent, name.Text, (int)limit.Value);
                    else editor.EditColumn(id.Value, name.Text, (int)limit.Value, originalName, originalLimit);
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { args.Cancel = true; error.Text = text.TranslateDiagnostic(ex.Message); }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
    private async void ManageBoards_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (document is null) return;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = "Name", MinWidth = 360, MaxHeight = 400 };
        void Populate() => list.ItemsSource = WorkspaceView.Boards(store.Current!).Select(x => new Choice(x.Id, x.Get<string>(Fields.Name))).ToArray();
        Populate();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = T("Select boards to delete, including all their tasks."), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 400 }; panel.Children.Add(error);
        var dialog = Dialog(T("Manage boards"), panel, T("Delete selected boards")); dialog.CloseButtonText = T("Close");
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var selected = list.SelectedItems.Cast<Choice>().ToArray();
            if (selected.Length == 0) { error.Text = T("Select at least one board."); return; }
            // Require a second deliberate click before deleting the selected boards.
            if (dialog.PrimaryButtonText != T("Confirm deletion of {0} board(s)", selected.Length))
            { dialog.PrimaryButtonText = T("Confirm deletion of {0} board(s)", selected.Length); error.Text = T("Press again to permanently delete the selected boards."); return; }
            var deferral = args.GetDeferral();
            try
            {
                await store.CommitAsync(editor => { foreach (var item in selected) editor.DeleteBoard(item.Id); });
                if (selected.Any(x => x.Id == draftBoardId)) CloseEditor();
                Populate(); error.Text = T("Boards deleted."); dialog.PrimaryButtonText = T("Delete selected boards");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { error.Text = text.TranslateDiagnostic(ex.Message); }
            finally { deferral.Complete(); }
        };
        list.SelectionChanged += (_, _) => { dialog.PrimaryButtonText = T("Delete selected boards"); error.Text = ""; };
        await dialog.ShowAsync();
    });
}
