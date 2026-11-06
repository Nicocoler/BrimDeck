# Water Reminder Design

Timed “drink water” reminders on BrimDeck’s collapsed Dynamic Island, reusing the existing Compact Alert channel.

## Goals

- Nudge the user at a fixed interval while BrimDeck is running.
- Feel native to BrimDeck: same compact-island alert presentation as quota warnings.
- Stay out of the way during fullscreen / do-not-disturb, and outside a user-chosen daily window.

## Non-goals (v1)

- Custom reminder copy
- Sound / system Toast
- “I drank” / snooze buttons
- Per-weekday schedules or multiple windows
- Reminding when the app is not running

## Behavior

| Setting | Default | Notes |
| --- | --- | --- |
| Enabled | `true` | Toggle in Settings |
| Interval | `45` minutes | Clamped to 1–180 |
| Daily window start | `09:00` | Local `"HH:mm"` |
| Daily window end | `22:00` | Local `"HH:mm"`; may be before start (cross-midnight) |

- On fire: show a Compact Alert on the collapsed island with fixed bilingual copy (“该喝水了” / “Time to drink water”), auto-dismiss after ~5 seconds (same duration as quota alerts).
- No click required.
- On app start: schedule one full interval from “now”; do **not** fire immediately.
- On settings change (enable, minutes, window): reschedule the next fire; do **not** catch up missed fires.
- Outside the daily window: sleep until the next valid fire (window open + interval rules); no busy polling.
- While `IsSuppressed` (fullscreen rule or user-hidden): if due, skip this cycle and schedule the next interval; do not queue a deferred popup for when the island returns.
- If a quota Compact Alert is already visible when water is due: wait until that alert clears, then show water once if still inside the window and not suppressed; otherwise drop this cycle.
- Line / indicator style: follow whatever Compact Alert already does for that shape.

## Architecture

```text
DeckSettings ──► WaterReminder (Core, pure) ──► next DueAt
                      ▲
MainWindow DispatcherTimer fires at DueAt
                      │
                      ▼
              Compact Alert (water kind) ──► auto-clear
```

### Settings (`DeckSettings`)

- `WaterReminderEnabled` (`bool`, default `true`)
- `WaterReminderMinutes` (`int`, default `45`, clamp 1–180 on set/load)
- `WaterReminderStart` / `WaterReminderEnd` (`string`, `"HH:mm"`, defaults `09:00` / `22:00`)

Persist through the existing `SettingsStore` / `settings.json` path. Invalid or missing time strings fall back to defaults on load.

### Core scheduler (`WaterReminder`)

Pure functions (unit-tested), no UI or timers.

**API:** `DateTimeOffset? NextDue(enabled, intervalMinutes, startHHmm, endHHmm, DateTimeOffset now)` → next due instant, or `null` when disabled.

**Window:** local clock times as half-open `[start, end)`. If `start == end`, treat as all-day. If `start > end`, the window crosses midnight (e.g. `22:00`–`07:00`).

**v1 scheduling rule** (locked by unit tests):

1. If disabled → `null`.
2. Let `candidate = now + interval`.
3. If `candidate` is inside the window → return `candidate`.
4. Otherwise set `candidate` to the next window-start at or after `candidate`, and return that.

Skipped cycles (suppressed, conflict dropped) use the same rule from the skip’s `now`, so they do not catch up.

### UI (`MainWindow`)

- One `DispatcherTimer` (or reuse a lightweight tick) armed to the next `DueAt`.
- On tick: if suppressed → reschedule skip; else if quota alert active → retry shortly after clear / on alert clear; else `ShowWaterAlert()` then reschedule.
- Extend Compact Alert enough to render a water notice without inventing a fake `AppEntry` quota row (dedicated kind or parallel info type). Layout should stay a short centered label in the collapsed surface.
- Clearing / hotkey dismiss behavior matches existing alert clear where applicable.

### Settings UI

- Small “Water reminder” group in the settings window (near general / notice controls).
- Controls: enable toggle, interval (minutes), start time, end time.
- Changing any control calls the same settings `Change` path and triggers reschedule on the main window.

### Localization

- Fixed alert string and settings labels via `Loc` (zh-CN + en-US).

## Testing

Core unit tests for `WaterReminder`:

- Disabled → no due time
- Inside window: next = now + interval
- Outside window: next lands at/after next window open
- Cross-midnight window
- Interval clamp / invalid times → safe defaults (if validated in Core helpers)

No UI automation required for v1.

## Implementation notes

- Prefer a small Core type + thin MainWindow wiring over scattering time math in the WPF layer.
- Do not add Toast, sounds, or tray balloons in v1.
- Quota alert ownership of Compact Alert stays primary; water is a second producer with the conflict rule above.
