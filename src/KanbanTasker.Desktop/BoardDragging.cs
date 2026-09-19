using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    // These moves are confined to the board. Pointer capture also keeps fast gestures and
    // drops onto collapsed columns independent of the shell's cross-application drag loop.
    private sealed class BoardDrag(FrameworkElement source, FrameworkElement visual, Guid id, bool isColumn, Guid workspace, Guid board, Pointer pointer, Point start, Rect bounds)
    {
        public FrameworkElement Source { get; } = source;
        public FrameworkElement Visual { get; } = visual;
        public Rect Bounds { get; } = bounds;
        public double OriginalOpacity { get; } = visual.Opacity;
        public Border? Preview { get; set; }
        public Guid Id { get; } = id;
        public bool IsColumn { get; } = isColumn;
        public Guid Workspace { get; } = workspace;
        public Guid Board { get; } = board;
        public Pointer Pointer { get; } = pointer;
        public Point Start { get; } = start;
        public Point Position { get; set; } = start;
        public bool Moving { get; set; }
    }
    private sealed record BoardDrop(Guid Column, int Index, Border Highlight, Thickness Edge);
    private BoardDrag? boardDrag;
    private readonly Dictionary<Guid, ListView> columnLists = [];
    private readonly DispatcherTimer dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Border? dragHighlight;
    private Brush? previousDragBrush;
    private Thickness previousDragThickness;

    private void InitializeBoardDragging()
    {
        dragScrollTimer.Tick += (_, _) => UpdateDragFeedback(scroll: true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Escape && boardDrag is not null) { CancelBoardDrag(); Render(); e.Handled = true; }
        }), true);
        Closed += (_, _) => CancelBoardDrag();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && boardDrag is not null)
            { CancelBoardDrag(); Render(); }
        };
    }
    private void EnableBoardDrag(FrameworkElement source, Guid id, bool isColumn, FrameworkElement? visual = null)
    {
        source.ManipulationMode = ManipulationModes.None;
        source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (working || document is null || boardId is null || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            CancelBoardDrag();
            var draggedVisual = visual ?? source;
            boardDrag = new(source, draggedVisual, id, isColumn, document.DocumentId, boardId.Value, e.Pointer,
                e.GetCurrentPoint(Root).Position, BoundsInRoot(draggedVisual));
            if (!source.CapturePointer(e.Pointer)) { boardDrag = null; return; }
            e.Handled = true;
        }), true);
        source.PointerMoved += (_, e) =>
        {
            if (boardDrag is not { } drag || drag.Pointer.PointerId != e.Pointer.PointerId) return;
            drag.Position = e.GetCurrentPoint(Root).Position;
            if (!drag.Moving && Math.Abs(drag.Position.X - drag.Start.X) + Math.Abs(drag.Position.Y - drag.Start.Y) >= 6)
            {
                drag.Moving = true; dragScrollTimer.Start();
                _ = ShowDragPreviewAsync(drag);
            }
            if (drag.Moving) { UpdateDragFeedback(scroll: false); e.Handled = true; }
        };
        source.PointerReleased += async (_, e) =>
        {
            if (boardDrag is not { } drag || drag.Pointer.PointerId != e.Pointer.PointerId) return;
            drag.Position = e.GetCurrentPoint(Root).Position;
            var target = drag.Moving ? FindBoardDrop(drag) : null;
            CancelBoardDrag(); e.Handled = true;
            if (target is not null)
                await RunAsync(() => store.CommitAsync(editor =>
                {
                    if (drag.IsColumn) editor.MoveColumn(drag.Id, target.Index);
                    else editor.MoveTask(drag.Id, target.Column, target.Index);
                }));
            else if (!drag.Moving && !drag.IsColumn && document is not null)
            {
                var task = WorkspaceView.AllTasks(document).FirstOrDefault(t => t.Id == drag.Id && t.BoardId == drag.Board);
                if (task is not null) await OpenEditorAsync(drag.Id, task.Get<Guid>(Fields.ColumnId));
            }
            Render();
        };
        source.PointerCanceled += (_, _) => { if (boardDrag?.Source == source) { CancelBoardDrag(); Render(); } };
        source.PointerCaptureLost += (_, _) => { if (boardDrag?.Source == source) { CancelBoardDrag(); Render(); } };
    }
    private async Task ShowDragPreviewAsync(BoardDrag drag)
    {
        // Capture the entire card/column before fading its original. Keeping that original
        // in place preserves pointer capture, layout, hit testing and the insertion marker.
        var bitmap = new RenderTargetBitmap();
        try
        {
            await bitmap.RenderAsync(drag.Visual);
            if (closed || boardDrag != drag || !drag.Moving || bitmap.PixelWidth == 0) return;
            drag.Preview = new Border
            {
                Child = new Image { Source = bitmap, Stretch = Stretch.Fill },
                Width = drag.Bounds.Width, Height = drag.Bounds.Height,
                Background = Root.Background, CornerRadius = new(drag.IsColumn ? 8 : 6),
                Opacity = .96, IsHitTestVisible = false
            };
            DragLayer.Children.Add(drag.Preview);
            drag.Visual.Opacity = .22;
            PositionDragPreview(drag);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // A removed visual or display reset can cancel rendering; the move still works.
            if (boardDrag == drag) drag.Visual.Opacity = .55;
        }
    }
    private static void PositionDragPreview(BoardDrag drag)
    {
        if (drag.Preview is null) return;
        Canvas.SetLeft(drag.Preview, drag.Bounds.X + drag.Position.X - drag.Start.X);
        Canvas.SetTop(drag.Preview, drag.Bounds.Y + drag.Position.Y - drag.Start.Y);
    }
    private Rect BoundsInRoot(FrameworkElement element) => element.TransformToVisual(Root)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private BoardDrop? FindBoardDrop(BoardDrag drag)
    {
        if (document?.DocumentId != drag.Workspace || boardId != drag.Board || !BoundsInRoot(BoardScroll).Contains(drag.Position)) return null;
        var columns = WorkspaceView.Columns(document, drag.Board).Select(c => c.Id).ToList();
        foreach (var border in ColumnsPanel.Children.OfType<Border>())
        {
            if (border.Tag is not Guid columnId || !columns.Contains(columnId)) continue;
            var bounds = BoundsInRoot(border);
            if (drag.Position.X < bounds.Left || drag.Position.X > bounds.Right) continue;
            if (drag.IsColumn)
            {
                var sourceIndex = columns.IndexOf(drag.Id);
                if (sourceIndex < 0) return null;
                var after = drag.Position.X > bounds.Left + bounds.Width / 2;
                var targetIndex = columns.IndexOf(columnId) + (after ? 1 : 0);
                if (sourceIndex < targetIndex) targetIndex--;
                return new(columnId, targetIndex, border, after ? new(1, 1, 3, 1) : new(3, 1, 1, 1));
            }
            var tasks = WorkspaceView.Tasks(document, columnId).Select(t => t.Id).ToList();
            var target = tasks.Count;
            Border highlight = border;
            var edge = new Thickness(1, 1, 1, 3);
            if (columnLists.TryGetValue(columnId, out var list))
            {
                // The heading, buttons and gap above the cards are one drop zone.
                // Anchor it to the viewport as well, so scrolling the list cannot
                // turn a header drop into an insertion among off-screen cards.
                var top = BoundsInRoot(list).Top;
                if (list.Items.OfType<ListViewItem>().FirstOrDefault() is { } first)
                    top = Math.Max(top, BoundsInRoot(first).Top);
                if (drag.Position.Y < top) return new(columnId, 0, border, new Thickness(2));
                foreach (var item in list.Items.OfType<ListViewItem>())
                    if (item.Tag is Guid taskId && tasks.IndexOf(taskId) is var itemIndex && itemIndex >= 0
                        && drag.Position.Y < BoundsInRoot(item).Top + item.ActualHeight / 2)
                    {
                        target = itemIndex;
                        if (item.Content is Border card) { highlight = card; edge = new(1, 3, 1, 1); }
                        break;
                    }
            }
            var original = tasks.IndexOf(drag.Id);
            if (original >= 0 && original < target) target--;
            return new(columnId, target, highlight, edge);
        }
        return null;
    }
    private void UpdateDragFeedback(bool scroll)
    {
        if (boardDrag is not { Moving: true } drag) return;
        PositionDragPreview(drag);
        if (scroll)
        {
            var bounds = BoundsInRoot(BoardScroll);
            if (bounds.Contains(drag.Position))
            {
                var delta = drag.Position.X < bounds.Left + 36 ? -24 : drag.Position.X > bounds.Right - 36 ? 24 : 0;
                if (delta != 0) BoardScroll.ChangeView(BoardScroll.HorizontalOffset + delta, null, null, true);
                if (!drag.IsColumn)
                    foreach (var list in columnLists.Values)
                    {
                        var listBounds = BoundsInRoot(list);
                        if (!listBounds.Contains(drag.Position)) continue;
                        var dy = drag.Position.Y < listBounds.Top + 36 ? -20 : drag.Position.Y > listBounds.Bottom - 36 ? 20 : 0;
                        var viewer = FindScrollViewer(list);
                        if (dy != 0 && viewer is not null) viewer.ChangeView(null, viewer.VerticalOffset + dy, null, true);
                    }
            }
        }
        ClearDragHighlight();
        if (FindBoardDrop(drag) is { } target)
        {
            dragHighlight = target.Highlight; previousDragBrush = dragHighlight.BorderBrush; previousDragThickness = dragHighlight.BorderThickness;
            dragHighlight.BorderBrush = Brush("AccentFillColorDefaultBrush"); dragHighlight.BorderThickness = target.Edge;
        }
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
    private void ClearDragHighlight()
    {
        if (dragHighlight is null) return;
        dragHighlight.BorderBrush = previousDragBrush; dragHighlight.BorderThickness = previousDragThickness; dragHighlight = null;
    }
    private void CancelBoardDrag()
    {
        var drag = boardDrag; boardDrag = null;
        dragScrollTimer.Stop(); ClearDragHighlight();
        DragLayer.Children.Clear();
        if (drag is not null) { drag.Visual.Opacity = drag.OriginalOpacity; drag.Source.ReleasePointerCaptures(); }
    }
}
