namespace KanbanTasker.Core;

public static class ReminderTime
{
    public static bool TryGet(TaskData task, out DateTimeOffset due, out DateTimeOffset at)
    {
        due = at = default;
        if (task.DueDate is null || task.DueTime is null || task.ReminderMinutes is null or < 0) return false;
        try
        {
            due = new DateTimeOffset(task.DueDate.Value.ToDateTime(task.DueTime.Value));
            at = due.AddMinutes(-task.ReminderMinutes.Value);
            return true;
        }
        catch (ArgumentException) { return false; } // Calendar/UTC boundaries cannot be scheduled by Windows.
    }
}
