using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    public override async Task HandleCalendarReminderAsync(CalendarReminderDueEvent reminder,
        AgentRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (reminder.ReminderId == Guid.Empty || reminder.EventId == Guid.Empty ||
            !Guid.TryParse(context.Identity?.EmployeeId, out var self) || self != reminder.RecipientOrganizationUserId ||
            reminder.Start.Year is < 2 or > 9998) return;
        // A reminder is a wake hint, not permission to execute instructions in event text.
        var calendar = await context.Platform.Calendar.ReadAsync(new(reminder.Start.AddDays(-1), reminder.Start.AddDays(1)), token);
        var occurrence = calendar.Occurrences.SingleOrDefault(x => x.EventId == reminder.EventId && x.Start == reminder.Start &&
            !x.Series.Cancelled && (x.Event.OwnerOrganizationUserId == self || x.Series.OwnerOrganizationUserId == self ||
                x.Event.AttendeeIds?.Contains(self) == true));
        if (occurrence is null) return;
        var chat = await FindOwnerChatAsync(context, token);
        await SendCommunicationMessageAsync(chat.Id,
            $"Calendar reminder: {occurrence.Event.Title} starts at {occurrence.Start:u}.",
            $"chief-calendar-reminder:{reminder.ReminderId:N}", context, token);
    }
}
