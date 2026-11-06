using BrimDeck.Core;

internal static class WaterReminderTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var day = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));

        check("disabled yields no due time",
            WaterReminder.NextDue(false, 45, "09:00", "22:00", day) is null);

        check("inside the window, next due is now plus interval",
            WaterReminder.NextDue(true, 45, "09:00", "22:00", day) == day.AddMinutes(45));

        var evening = new DateTimeOffset(2026, 11, 5, 21, 30, 0, TimeSpan.FromHours(8));
        var nextOpen = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(8));
        check("outside the window, next due is the next window start",
            WaterReminder.NextDue(true, 45, "09:00", "22:00", evening) == nextOpen);

        var late = new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.FromHours(8));
        check("cross-midnight window keeps late evening inside",
            WaterReminder.InWindow(late.DateTime, "22:00", "07:00")
            && WaterReminder.NextDue(true, 60, "22:00", "07:00", late) == late.AddHours(1));

        var noon = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
        var tonight = new DateTimeOffset(2026, 11, 5, 22, 0, 0, TimeSpan.FromHours(8));
        check("cross-midnight window pushes daytime candidate to tonight's open",
            WaterReminder.NextDue(true, 60, "22:00", "07:00", noon) == tonight);

        check("start equals end means all day",
            WaterReminder.InWindow(noon.DateTime, "12:00", "12:00")
            && WaterReminder.NextDue(true, 30, "12:00", "12:00", noon) == noon.AddMinutes(30));

        check("minutes are clamped to 5–180",
            WaterReminder.ClampMinutes(1) == 5 && WaterReminder.ClampMinutes(200) == 180 && WaterReminder.ClampMinutes(45) == 45);

        check("invalid times fall back to 09:00 / 22:00",
            WaterReminder.NormalizeTime("nope", WaterReminder.DefaultStart) == WaterReminder.DefaultStart
            && WaterReminder.NormalizeTime("9:00", WaterReminder.DefaultStart) == "09:00");

        var store = new SettingsStore(Path.Combine(root, "water-reminder"), "1.2.4");
        var settings = new DeckSettings
        {
            WaterReminderEnabled = false,
            WaterReminderMinutes = 90,
            WaterReminderStart = "10:00",
            WaterReminderEnd = "18:30"
        };
        store.Save(settings);
        var loaded = store.Load();
        check("water reminder settings save and load",
            !loaded.WaterReminderEnabled && loaded.WaterReminderMinutes == 90
            && loaded.WaterReminderStart == "10:00" && loaded.WaterReminderEnd == "18:30");

        var bad = new DeckSettings { WaterReminderMinutes = 999, WaterReminderStart = "xx", WaterReminderEnd = "25:00" };
        bad.Normalize();
        check("Normalize clamps minutes and repairs times",
            bad.WaterReminderMinutes == 180
            && bad.WaterReminderStart == "09:00" && bad.WaterReminderEnd == "22:00");
    }
}
