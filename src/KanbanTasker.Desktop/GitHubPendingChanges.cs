using KanbanTasker.Core;
using KanbanTasker.Core.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    // Presentation only: never add the preview to the workspace, cache or write queue.
    // Pin the confirmed view until the write AND its verification finish. Intermediate
    // source notifications must not move the original underneath the pending preview.
    private sealed record GitHubPendingChange(Guid BoardId, GitHubProjectSnapshot Project, GitHubView View,
        Guid? CardId, TaskData Preview, int? Index);
    private GitHubPendingChange? githubPending;
    private bool ShowingGitHubPending => githubPending is { } pending && pending.BoardId == boardId;
    private (GitHubProjectSnapshot Project, GitHubView View)? PresentedGitHubBoard => ShowingGitHubPending
        ? (githubPending!.Project, githubPending.View) : boardId is { } id ? GitHubBoard(id) : null;

    private async Task WithGitHubPendingAsync(Guid? cardId, GitHubEdit edit, int? index, Func<Task> write)
    {
        var entry = GitHubBoard(boardId!.Value)!.Value;
        var card = entry.Project.Cards.FirstOrDefault(c => GitHubIdentity.Card(entry.Project.Id,c.Id) == cardId);
        var preview = new TaskData
        {
            BoardId=boardId.Value,
            ColumnId=GitHubIdentity.Column(entry.Project.Id,edit.ChangeStatus ? edit.StatusId : card?.StatusId),
            Title=edit.Title ?? card?.Title ?? "", Description=edit.Body ?? card?.Body ?? ""
        };
        githubPending = new(boardId.Value,entry.Project,entry.View,cardId,preview,index);
        try { Render(); await write(); }
        finally
        {
            githubPending = null;
            if (!closed) Render();
        }
    }

    private void AddGitHubPendingPreview(ListView list, Guid column)
    {
        if (!ShowingGitHubPending || githubPending is not { } pending || pending.Preview.ColumnId != column) return;
        var items = list.Items.Cast<ListViewItem>().ToList();
        int position;
        if (pending.Index is { } requested)
        {
            var siblings = items.Where(i => !Equals(i.Tag,pending.CardId)).ToArray();
            var index = Math.Clamp(requested,0,siblings.Length);
            position = index == 0 ? 0 : items.IndexOf(siblings[index-1])+1;
        }
        else
        {
            var original = items.FindIndex(i => Equals(i.Tag,pending.CardId));
            position = original < 0 ? items.Count : original+1;
        }
        var body = new StackPanel { Spacing=7 };
        body.Children.Add(new TextBlock { Text=pending.Preview.Title, TextWrapping=TextWrapping.Wrap,
            FontWeight=Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(pending.Preview.Description)) body.Children.Add(new TextBlock
        {
            Text=pending.Preview.Description, TextWrapping=TextWrapping.Wrap, MaxLines=4,
            TextTrimming=TextTrimming.CharacterEllipsis, Foreground=Brush("TextFillColorSecondaryBrush")
        });
        body.Children.Add(new TextBlock { Text=T("Waiting for GitHub…"), FontSize=12, TextWrapping=TextWrapping.Wrap });
        var preview = new Border
        {
            Child=body, Opacity=0.55, Padding=new(12), CornerRadius=new(6), BorderThickness=new(1),
            BorderBrush=Brush("CardStrokeColorDefaultBrush"), Background=Brush("CardBackgroundFillColorDefaultBrush")
        };
        var item = new ListViewItem
        {
            Name="GitHubPendingPreview", Content=preview, IsHitTestVisible=false, IsTabStop=false,
            Margin=new(0,0,0,8), Padding=new(0), HorizontalContentAlignment=HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(item,pending.Preview.Title+", "+T("Waiting for GitHub…"));
        list.Items.Insert(position,item);
    }
}
