namespace BrimDeck.Core;

public static class WaterReminder
{
    public const string DefaultStart = "09:00", DefaultEnd = "22:00";
    public const int DefaultMinutes = 45, MinMinutes = 5, MaxMinutes = 180;

    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    public static string NormalizeTime(string? text, string fallback)
        => PanelThemes.ParseTimeOfDay(text) is { } time ? PanelThemes.FormatTimeOfDay(time) : fallback;

    public static bool InWindow(DateTime local, string startHHmm, string endHHmm)
    {
        var start = PanelThemes.ParseTimeOfDay(NormalizeTime(startHHmm, DefaultStart))!.Value;
        var end = PanelThemes.ParseTimeOfDay(NormalizeTime(endHHmm, DefaultEnd))!.Value;
        var now = local.TimeOfDay;
        if (start == end) return true;
        return start < end ? now >= start && now < end : now >= start || now < end;
    }

    public static DateTimeOffset? NextDue(bool enabled, int intervalMinutes, string startHHmm, string endHHmm, DateTimeOffset now)
    {
        if (!enabled) return null;
        var interval = TimeSpan.FromMinutes(ClampMinutes(intervalMinutes));
        var candidate = now + interval;
        if (InWindow(candidate.LocalDateTime, startHHmm, endHHmm)) return candidate;
        return NextWindowStart(candidate, startHHmm, endHHmm);
    }

    static DateTimeOffset NextWindowStart(DateTimeOffset from, string startHHmm, string endHHmm)
    {
        var start = PanelThemes.ParseTimeOfDay(NormalizeTime(startHHmm, DefaultStart))!.Value;
        var local = from.LocalDateTime;
        var today = local.Date + start;
        var open = today >= local ? today : today.AddDays(1);
        return new DateTimeOffset(open, from.Offset);
    }
}
