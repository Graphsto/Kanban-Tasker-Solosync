using KanbanTasker.Core;
using KanbanTasker.Localization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace KanbanTasker.Desktop;

/// <summary>Windows owns the schedule, so reminders survive closing the app. No cloud or background service.</summary>
internal sealed class ReminderService
{
    private string? previous;
    public string? Update(WorkspaceDocument document, TextCatalog text)
    {
        var desired = WorkspaceView.AllTasks(document).Select(TaskData.From)
            .Select(t => (Task: t, Valid: ReminderTime.TryGet(t, out var due, out var at), Due: due, At: at))
            .Where(t => t.Valid)
            .Where(t => t.At > DateTimeOffset.Now.AddSeconds(5)).OrderBy(t => t.At).ToArray();
        var fingerprint = text.Language + ":" + document.DocumentId + ":" + string.Join("|", desired.Select(t => $"{t.Task.Id}:{t.At:O}:{t.Task.Title}:{t.Task.Description}"));
        if (previous == fingerprint) return null;
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var scheduled = notifier.GetScheduledToastNotifications();
            var next = desired.ToDictionary(t => Tag(document.DocumentId, t.Task.Id));
            foreach (var old in scheduled.Where(x => x.Group == "KanbanTasker"))
            {
                if (!next.TryGetValue(old.Tag, out var item) || old.DeliveryTime != item.At || old.Content.GetXml() != Payload(item.Task, item.Due, text).GetXml())
                    notifier.RemoveFromSchedule(old);
                else next.Remove(old.Tag);
            }
            foreach (var (tag, item) in next)
                notifier.AddToSchedule(new ScheduledToastNotification(Payload(item.Task, item.Due, text), item.At) { Tag = tag, Group = "KanbanTasker" });
            previous = fingerprint;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Unpackaged development runs may not have notification identity; installed MSIX runs do.
            previous = null;
            return desired.Length == 0 ? null : text.Get("Reminders could not be scheduled. Install the MSIX package and check Windows notification settings.") + " " + ex.Message;
        }
    }
    private static string Tag(Guid document, Guid task) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{document}:{task}")))[..16];
    private static XmlDocument Payload(TaskData task, DateTimeOffset due, TextCatalog text)
    {
        string T(string key, params object?[] args) => SecurityElement.Escape(text.Get(key, args))!;
        var xml = new XmlDocument();
        xml.LoadXml($"""
            <toast scenario="reminder">
              <visual><binding template="ToastGeneric">
                <text>{SecurityElement.Escape(task.Title)}</text>
                <text>{SecurityElement.Escape(task.Description)}</text>
                <text>{T("Due {0:g}", due.LocalDateTime)}</text>
              </binding></visual>
              <actions>
                <input id="snooze" type="selection" defaultInput="15">
                  <selection id="5" content="{T("5 minutes")}"/><selection id="15" content="{T("15 minutes")}"/>
                  <selection id="60" content="{T("1 hour")}"/><selection id="240" content="{T("4 hours")}"/><selection id="1440" content="{T("1 day")}"/>
                </input>
                <action activationType="system" arguments="snooze" hint-inputId="snooze" content="{T("Snooze")}"/>
                <action activationType="system" arguments="dismiss" content="{T("Dismiss")}"/>
              </actions>
            </toast>
            """);
        return xml;
    }
}
