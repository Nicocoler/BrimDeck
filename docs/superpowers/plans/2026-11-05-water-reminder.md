# Water Reminder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a timed drink-water Compact Alert on BrimDeck’s collapsed island, with enable/interval/daily-window settings.

**Architecture:** Pure `WaterReminder` scheduler in Core (unit-tested) computes the next due time from settings. Each `MainWindow` arms a `DispatcherTimer` to that instant, shows a Compact Alert of kind Water (short centered label), and reschedules. Settings live on `DeckSettings` and a Notifications group in `SettingsWindow`.

**Tech Stack:** C# / .NET 10, WPF, existing `Loc` + `PanelThemes.ParseTimeOfDay`, custom console test runner in `tests/BrimDeck.Tests`.

**Spec:** `docs/superpowers/specs/2026-11-05-water-reminder-design.md`

---

## File map

| File | Role |
| --- | --- |
| `src/BrimDeck.Core/WaterReminder.cs` | Pure window + next-due math |
| `src/BrimDeck.Core/Settings.cs` | Persist enable / minutes / start / end; normalize |
| `tests/BrimDeck.Tests/WaterReminderTests.cs` | Scheduler + settings round-trip checks |
| `tests/BrimDeck.Tests/Program.cs` | Register the new suite |
| `src/BrimDeck/MainWindow.xaml.cs` | Timer, fire/skip/conflict, `CompactAlertInfo` water kind |
| `src/BrimDeck/MainWindow.CompactLayout.cs` | Render water alert (label only, no quota ring) |
| `src/BrimDeck/SettingsWindow.cs` | Settings UI + page reset |

---

### Task 1: Core scheduler + failing tests

**Files:**
- Create: `src/BrimDeck.Core/WaterReminder.cs`
- Create: `tests/BrimDeck.Tests/WaterReminderTests.cs`
- Modify: `tests/BrimDeck.Tests/Program.cs`

- [ ] **Step 1: Write the failing test suite**

Create `tests/BrimDeck.Tests/WaterReminderTests.cs`:

```csharp
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
```

In `tests/BrimDeck.Tests/Program.cs`, next to `OffPeakTests.Run(root, Check);` add:

```csharp
WaterReminderTests.Run(root, Check);
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj -c Release
```

Expected: FAIL / compile error — `WaterReminder` type and `DeckSettings` water properties do not exist yet.

- [ ] **Step 3: Implement `WaterReminder`**

Create `src/BrimDeck.Core/WaterReminder.cs`:

```csharp
using System.Globalization;

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
```

- [ ] **Step 4: Add settings fields + normalize**

In `src/BrimDeck.Core/Settings.cs`, immediately after `public bool QuotaAlerts { get; set; } = true;`:

```csharp
    // Timed drink-water notice on the collapsed island (WaterReminder).
    public bool WaterReminderEnabled { get; set; } = true;
    public int WaterReminderMinutes { get; set; } = WaterReminder.DefaultMinutes;
    public string WaterReminderStart { get; set; } = WaterReminder.DefaultStart;
    public string WaterReminderEnd { get; set; } = WaterReminder.DefaultEnd;
```

In `DeckSettings.Normalize()`, after the `QuotaAlerts` area is fine; place near other clamps (e.g. after `AnimationDuration` clamps):

```csharp
        WaterReminderMinutes = WaterReminder.ClampMinutes(WaterReminderMinutes);
        WaterReminderStart = WaterReminder.NormalizeTime(WaterReminderStart, WaterReminder.DefaultStart);
        WaterReminderEnd = WaterReminder.NormalizeTime(WaterReminderEnd, WaterReminder.DefaultEnd);
```

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj -c Release
```

Expected: all checks PASS, including the new water-reminder lines.

- [ ] **Step 6: Commit**

```powershell
git add src/BrimDeck.Core/WaterReminder.cs src/BrimDeck.Core/Settings.cs tests/BrimDeck.Tests/WaterReminderTests.cs tests/BrimDeck.Tests/Program.cs
git commit -m "喝水提醒：Core 调度器与设置字段"
```

---

### Task 2: Compact Alert water kind + MainWindow timer

**Files:**
- Modify: `src/BrimDeck/MainWindow.xaml.cs`
- Modify: `src/BrimDeck/MainWindow.CompactLayout.cs`

- [ ] **Step 1: Extend `CompactAlertInfo` and layout for water**

In `MainWindow.xaml.cs`, replace the alert record and related fields:

```csharp
    private CompactAlertInfo? _alert;
    private bool _waterPending;
    private readonly DispatcherTimer _waterTimer = new();
    // ...
    private sealed record CompactAlertInfo(string Name, string Label, double Used, DateTimeOffset? ResetAt, AppEntry? Entry = null, bool Water = false);
```

Update quota construction sites that used `new CompactAlertInfo(entry, entry.Name, ...)` to:

```csharp
new CompactAlertInfo(entry.Name, MetricLabel(quota) + " ", Math.Clamp(used, 0, 100), quota.ResetAt, entry)
```

And `ShowAlert(AppEntry entry, string label, double used, DateTimeOffset? resetAt)`:

```csharp
=> ShowAlert(new CompactAlertInfo(entry.Name, label + " ", used, resetAt, entry));
```

In `MainWindow.CompactLayout.cs` `LayoutCompactAlert`, at the start of the build path (after the early-return cache checks, before building quota name/used/reset), add a water branch:

```csharp
        if (alert.Water)
        {
            double scale = CompactMusicLayout.SafeScale(shape.Scale);
            double font = Math.Max(1, CompactMusicLayout.Half(11.5 * scale)), line = Math.Max(1, CompactMusicLayout.Whole(16 * scale));
            var text = UI.Fixed(alert.Name, font, line, UI.Primary, FontWeights.SemiBold);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.TextAlignment = TextAlignment.Center;
            text.Tag = AlertRegionTag;
            CompactAlert.Children.Add(text);
            return;
        }
```

Where quota layout uses `alert.Entry`, use `alert.Entry!` only on the quota path (water never reaches those lines). Update `PanelTheme.QuotaTheme(alert.Entry)` accordingly — only inside the non-water branch where `Entry` is non-null.

- [ ] **Step 2: Wire water timer lifecycle**

In the `MainWindow` constructor (near `_alertTimer` setup):

```csharp
        _waterTimer.Tick += (_, _) => OnWaterDue();
```

In `Closed` handler, also `_waterTimer.Stop();`.

Add methods (near alert helpers):

```csharp
    internal void ShowWaterAlert()
        => ShowAlert(new CompactAlertInfo(Loc.T("该喝水了", "Time to drink water"), "", 0, null, Water: true));

    private void ScheduleWaterReminder()
    {
        _waterTimer.Stop();
        var due = WaterReminder.NextDue(
            Settings.WaterReminderEnabled,
            Settings.WaterReminderMinutes,
            Settings.WaterReminderStart,
            Settings.WaterReminderEnd,
            DateTimeOffset.Now);
        if (due is null) return;
        var delay = due.Value - DateTimeOffset.Now;
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
        // DispatcherTimer Interval is capped practically; clamp to 24h chunks if needed.
        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
        _waterTimer.Interval = delay;
        _waterTimer.Start();
    }

    private void OnWaterDue()
    {
        _waterTimer.Stop();
        if (!Settings.WaterReminderEnabled) return;
        if (IsSuppressed)
        {
            ScheduleWaterReminder();
            return;
        }
        if (_alert is { Water: false })
        {
            _waterPending = true;
            return;
        }
        ShowWaterAlert();
        ScheduleWaterReminder();
    }

    private void FlushWaterPending()
    {
        if (!_waterPending) return;
        _waterPending = false;
        if (!Settings.WaterReminderEnabled || IsSuppressed) { ScheduleWaterReminder(); return; }
        if (!WaterReminder.InWindow(DateTime.Now, Settings.WaterReminderStart, Settings.WaterReminderEnd))
        { ScheduleWaterReminder(); return; }
        ShowWaterAlert();
        ScheduleWaterReminder();
    }
```

Update `_alertTimer.Tick` so that after clearing a quota alert it flushes pending water:

```csharp
_alertTimer.Tick += (_, _) =>
{
    bool wasQuota = _alert is { Water: false };
    _alertTimer.Stop(); _alert = null; RenderCompact();
    if (!_expanded) SetExpanded(false);
    if (wasQuota) FlushWaterPending();
};
```

Also call `FlushWaterPending()` at the end of `ClearAlert()` when the cleared alert was quota (track before nulling).

In `ApplySettings`, after the quota-alerts clear line:

```csharp
        if (!Settings.WaterReminderEnabled)
        {
            _waterPending = false;
            if (_alert is { Water: true }) ClearAlert();
        }
        ScheduleWaterReminder();
```

When `ShowAlert` is called for a new quota while a water alert is showing, existing behavior replaces `_alert` — acceptable for v1.

- [ ] **Step 3: Build the app to confirm compile**

Run:

```powershell
dotnet build src/BrimDeck/BrimDeck.csproj -c Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/BrimDeck/MainWindow.xaml.cs src/BrimDeck/MainWindow.CompactLayout.cs
git commit -m "喝水提醒：岛上 Compact Alert 与定时器"
```

---

### Task 3: Settings UI

**Files:**
- Modify: `src/BrimDeck/SettingsWindow.cs`

- [ ] **Step 1: Add the Notifications group controls**

In `Pages()`, expand the existing Notifications `Group` (currently only `QuotaAlerts`) to include water reminder controls. Replace that `Group(...)` call with:

```csharp
        SectionTitle(Loc.T("提示", "Notifications"));
        var waterInterval = SliderRow(
            Loc.T("提醒间隔", "Reminder interval"),
            S.WaterReminderMinutes, WaterReminder.MinMinutes, WaterReminder.MaxMinutes, 5,
            Loc.T("分钟", "min"),
            value => Change(s => s.WaterReminderMinutes = (int)value));
        var waterWindow = WaterReminderWindowRow();
        void EnableWater(bool on)
        {
            foreach (var part in waterInterval.Children.OfType<FrameworkElement>().Where(c => c is not Slider))
                part.Opacity = on ? 1 : .4;
            waterInterval.IsEnabled = on;
            waterWindow.IsEnabled = on;
            waterWindow.Opacity = on ? 1 : .4;
        }
        EnableWater(S.WaterReminderEnabled);
        Group(
            Toggle(Loc.T("预警和告警提示", "Warning and critical notices"), S.QuotaAlerts, v => Change(s => s.QuotaAlerts = v), help:
                Loc.T($"开启后，某个应用的已用比例升到预警（{AppPresets.WarningPercent:0}%）或告警（{AppPresets.CriticalPercent:0}%）时，收起的刘海或胶囊会短暂显示该应用的名称、已用比例和重置时间，约 5 秒后恢复。",
                    $"When on, and an app's used share rises to the warning ({AppPresets.WarningPercent:0}%) or critical ({AppPresets.CriticalPercent:0}%) level, the collapsed notch or capsule briefly shows the app's name, used share and reset time, then returns after about 5 seconds.")),
            Toggle(Loc.T("喝水提醒", "Water reminder"), S.WaterReminderEnabled, v =>
            {
                Change(s => s.WaterReminderEnabled = v);
                EnableWater(v);
            }, help: Loc.T("开启后，在设定的每日时段内按间隔在收起的刘海或胶囊上短暂提示喝水，约 5 秒后消失。全屏免打扰时跳过本轮。",
                "When on, during the daily window the collapsed notch or capsule briefly nudges you to drink water, then clears after about 5 seconds. Full-screen do-not-disturb skips that cycle.")),
            waterInterval,
            waterWindow);
```

Add a private helper on `SettingsWindow` (same partial / file as `Pages`):

```csharp
    private FrameworkElement WaterReminderWindowRow()
    {
        var start = PanelThemes.ParseTimeOfDay(S.WaterReminderStart) ?? PanelThemes.ParseTimeOfDay(WaterReminder.DefaultStart)!.Value;
        var end = PanelThemes.ParseTimeOfDay(S.WaterReminderEnd) ?? PanelThemes.ParseTimeOfDay(WaterReminder.DefaultEnd)!.Value;
        var steps = Enumerable.Range(0, 48).Select(i => TimeSpan.FromMinutes(30 * i)).ToList();
        (ComboBox Box, List<TimeSpan> Times) Pick(TimeSpan value, string name)
        {
            var times = steps.Contains(value) ? steps : [.. steps.Append(value).Order()];
            var box = new ComboBox
            {
                Width = 84,
                Style = (Style)FindResource("SettingsCombo"),
                ItemsSource = times.Select(PanelThemes.FormatTimeOfDay).ToList(),
                SelectedIndex = times.IndexOf(value) is var at and >= 0 ? at : 0,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            box.DropDownOpened += (_, _) => box.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowChosen(box));
            AutomationProperties.SetName(box, name);
            PopupMotion.Attach(box);
            return (box, times);
        }
        var (from, fromTimes) = Pick(start, Loc.T("开始", "Start"));
        var (to, toTimes) = Pick(end, Loc.T("结束", "End"));
        void Save()
        {
            var a = PanelThemes.FormatTimeOfDay(fromTimes[Math.Max(0, from.SelectedIndex)]);
            var b = PanelThemes.FormatTimeOfDay(toTimes[Math.Max(0, to.SelectedIndex)]);
            Change(s => { s.WaterReminderStart = a; s.WaterReminderEnd = b; });
        }
        from.SelectionChanged += (_, _) => Save();
        to.SelectionChanged += (_, _) => Save();
        var dash = TextLine("–", 12, TextSecondary);
        dash.Margin = new Thickness(8, 0, 8, 0);
        dash.VerticalAlignment = VerticalAlignment.Center;
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        pair.Children.Add(from); pair.Children.Add(dash); pair.Children.Add(to);
        return Row(Loc.T("每日时段", "Daily window"), pair, help: () => Loc.T(
            "只在这段本地时间内提醒。结束早于开始时视为跨午夜（例如 22:00–07:00）。起止相同表示全天。",
            "Remind only during this local window. If end is earlier than start, the window crosses midnight (for example 22:00–07:00). Equal times mean all day."));
    }
```

Note: `ShowChosen` already exists on `SettingsWindow` / themes partial — call the same static helper used by `DaylightRow`. If it is `private` on another partial in `SettingsThemes.cs`, either make it `internal static` accessible or duplicate the small scroll helper locally. Prefer reusing: change `ShowChosen` to `internal static` if needed.

- [ ] **Step 2: Reset defaults on Apps/Notifications page**

In the reset `switch`, `case 3:` block, also restore water fields:

```csharp
case 3:{var previous=s.Apps;s.QuotaAlerts=defaults.QuotaAlerts;s.WaterReminderEnabled=defaults.WaterReminderEnabled;s.WaterReminderMinutes=defaults.WaterReminderMinutes;s.WaterReminderStart=defaults.WaterReminderStart;s.WaterReminderEnd=defaults.WaterReminderEnd;s.IncludeWsl=defaults.IncludeWsl;s.Apps=defaults.Apps;PanelThemes.CarryAppChoices(s,previous);break;}
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/BrimDeck/BrimDeck.csproj -c Release
```

Expected: success.

- [ ] **Step 4: Commit**

```powershell
git add src/BrimDeck/SettingsWindow.cs src/BrimDeck/SettingsThemes.cs
git commit -m "喝水提醒：设置页开关、间隔与每日时段"
```

---

### Task 4: Manual smoke + final verification

**Files:** none required unless bugs found.

- [ ] **Step 1: Re-run unit tests**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 2: Manual smoke (local run)**

```powershell
dotnet run --project src/BrimDeck/BrimDeck.csproj -c Release
```

Checklist:

1. Settings → 应用/提示：见「喝水提醒」默认开、45 分钟、09:00–22:00。
2. 将间隔临时改为 **5** 分钟，保存后等待 ≤5 分钟：收起岛显示「该喝水了」，约 5 秒后消失。
3. 关掉喝水提醒：不再出现。
4. 打开提醒，把每日时段设到当前时间之外：到点不弹；改回包含现在的时段后，按间隔恢复。
5. （可选）触发配额告警时若喝水同时到期：先配额，结束后再喝水一次。

- [ ] **Step 3: Commit any fixes** (only if smoke found bugs)

```powershell
git add -A
git commit -m "喝水提醒：修复冒烟问题"
```

If nothing to fix, skip this commit.

---

## Spec coverage (self-review)

| Spec requirement | Task |
| --- | --- |
| Default on, 45 min, 09:00–22:00 | Task 1 settings defaults |
| Interval clamp 5–180 | Task 1 `ClampMinutes` + UI slider |
| Daily window + cross-midnight + all-day | Task 1 `InWindow` / `NextDue` |
| Compact Alert auto-dismiss ~5s | Task 2 reuse `_alertTimer` |
| No fire on startup (full interval first) | Task 2 `ScheduleWaterReminder` via `NextDue(now+interval)` |
| Settings change reschedules, no catch-up | Task 2 `ApplySettings` → `ScheduleWaterReminder` |
| Suppressed → skip cycle | Task 2 `OnWaterDue` |
| Quota conflict → pending then flush | Task 2 `_waterPending` / `FlushWaterPending` |
| Settings UI + Loc | Task 3 |
| No sound / toast / custom copy / snooze | Out of scope — not in plan |
| Core unit tests | Task 1 |

No TBD placeholders. Property names consistent: `WaterReminderEnabled|Minutes|Start|End`, `WaterReminder.NextDue/InWindow/ClampMinutes/NormalizeTime`.
