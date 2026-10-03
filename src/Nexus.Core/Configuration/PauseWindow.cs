namespace Nexus.Core.Configuration;

/// <summary>
/// Local-time window in which collections must not run (site backup, SQL maintenance).
/// Empty <see cref="Days"/> means every day. A window may cross midnight (Start &gt; End).
/// </summary>
public sealed class PauseWindow
{
    public string Name { get; set; } = "";
    public List<DayOfWeek> Days { get; set; } = [];
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }

    public bool Contains(DateTime localTime)
    {
        var time = TimeOnly.FromDateTime(localTime);
        if (Start == End)
        {
            return false;
        }

        if (Start < End)
        {
            return AppliesTo(localTime.DayOfWeek) && time >= Start && time < End;
        }

        // Crosses midnight: the part after midnight belongs to the previous day's window.
        if (time >= Start)
        {
            return AppliesTo(localTime.DayOfWeek);
        }

        return time < End && AppliesTo(localTime.AddDays(-1).DayOfWeek);
    }

    private bool AppliesTo(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);
}
