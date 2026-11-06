using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // After the enhanced-sensors page; listed in its own "Reminders" navigation group.
    internal const int WaterReminderPage = 11;

    private void WaterReminderSettings()
    {
        SectionTitle(Loc.T("定时提醒", "Timed reminder"));
        var waterInterval = SliderRow(
            Loc.T("提醒间隔", "Reminder interval"),
            S.WaterReminderMinutes, WaterReminder.MinMinutes, WaterReminder.MaxMinutes, 1,
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
            Toggle(Loc.T("启用喝水提醒", "Enable water reminder"), S.WaterReminderEnabled, v =>
            {
                Change(s => s.WaterReminderEnabled = v);
                EnableWater(v);
            }, help: Loc.T("开启后，在设定的每日时段内按间隔在收起的刘海或胶囊上短暂提示喝水，约 5 秒后消失。全屏免打扰时跳过本轮。指示条形态下会临时展开以便看清提醒。",
                "When on, during the daily window the collapsed notch or capsule briefly nudges you to drink water, then clears after about 5 seconds. Full-screen do-not-disturb skips that cycle. On the indicator, the surface opens briefly so the notice can be read.")),
            waterInterval,
            waterWindow);
    }
}
