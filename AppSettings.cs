using System;
using System.IO;
using System.Text.Json;

namespace WinIsland
{
    public class AppSettings
    {
        public bool BluetoothNotificationEnabled { get; set; } = true;
        public bool UsbNotificationEnabled { get; set; } = true;
        public bool MessageNotificationEnabled { get; set; } = true;
        public bool NotificationDebugLoggingEnabled { get; set; } = false;
        public bool NotificationAllowAllApps { get; set; } = false;
        public string NotificationAppWhitelist { get; set; } = "QQ, 微信, WeChat, TIM, 腾讯";
        public bool ShowMediaPlayer { get; set; } = true;
        public bool ShowVisualizer { get; set; } = true;
        public bool DrinkWaterEnabled { get; set; } = false;
        public int DrinkWaterIntervalMinutes { get; set; } = 30;
        public bool TodoEnabled { get; set; } = false;
        public bool SystemStatsEnabled { get; set; } = false;
        public double IslandOpacity { get; set; } = 1.0;
        public double StandbyWidth { get; set; } = 120.0;
        public double StandbyHeight { get; set; } = 35.0;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public string DrinkWaterStartTime { get; set; } = "09:00";
        public string DrinkWaterEndTime { get; set; } = "22:00";
        public DrinkWaterMode DrinkWaterMode { get; set; } = DrinkWaterMode.Interval;
        public List<string> CustomDrinkWaterTimes { get; set; } = new List<string>();
        public List<TodoItem> TodoList { get; set; } = new List<TodoItem>();
        public bool SedentaryReminderEnabled { get; set; } = false;
        public int SedentaryReminderIntervalMinutes { get; set; } = 60;
        public bool FocusModeEnabled { get; set; } = false;
        public string FocusModeAllowedApps { get; set; } = "";
        public bool FocusModeBluetoothEnabled { get; set; } = false;
        public bool FocusModeUsbEnabled { get; set; } = false;
        public bool FocusModeMessageEnabled { get; set; } = true;
        public bool FocusModeAllowAllApps { get; set; } = false;
        public bool FocusModeShowMediaPlayer { get; set; } = false;
        public bool FocusModeShowVisualizer { get; set; } = false;
        public bool FocusModeSystemStatsEnabled { get; set; } = false;
        public bool FocusModeDrinkWaterEnabled { get; set; } = false;
        public bool FocusModeSedentaryEnabled { get; set; } = false;
        public bool FocusModeTodoEnabled { get; set; } = false;
        public List<FocusSession> FocusHistory { get; set; } = new List<FocusSession>();

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }

    public class TodoItem
    {
        public DateTime ReminderTime { get; set; }
        public string Content { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class FocusSession
    {
        public DateTime StartTime { get; set; }
        public double DurationSeconds { get; set; }
    }
}
