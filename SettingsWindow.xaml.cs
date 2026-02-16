using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Linq;

namespace WinIsland
{
    public partial class SettingsWindow : Window
    {
        private bool _isLoading;

        public SettingsWindow()
        {
            _isLoading = true;
            InitializeComponent();
            LoadSettings();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void LoadSettings()
        {
            _isLoading = true;
            var settings = AppSettings.Load();
            
            // 系统设置
            ChkStartWithWindows.IsChecked = IsStartupEnabled();
            ChkSystemStats.IsChecked = settings.SystemStatsEnabled;
            ChkBluetoothNotification.IsChecked = settings.BluetoothNotificationEnabled;
            ChkUsbNotification.IsChecked = settings.UsbNotificationEnabled;
            ChkMessageNotification.IsChecked = settings.MessageNotificationEnabled;
            ChkNotificationAllowAllApps.IsChecked = settings.NotificationAllowAllApps;
            TxtNotificationWhitelist.Text = settings.NotificationAppWhitelist ?? "";
            ChkNotificationDebugLogging.IsChecked = settings.NotificationDebugLoggingEnabled;
            ChkShowMediaPlayer.IsChecked = settings.ShowMediaPlayer;
            ChkShowVisualizer.IsChecked = settings.ShowVisualizer;
            SldIslandOpacity.Value = Math.Clamp(settings.IslandOpacity, 0.2, 1.0);
            SldIslandWidth.Value = Math.Clamp(settings.StandbyWidth, 80, 200);
            SldIslandHeight.Value = Math.Clamp(settings.StandbyHeight, 25, 50);

            // 喝水提醒
            ChkDrinkWater.IsChecked = settings.DrinkWaterEnabled;
            TxtDrinkWaterInterval.Text = settings.DrinkWaterIntervalMinutes.ToString();
            TxtDrinkStartTime.Text = settings.DrinkWaterStartTime;
            TxtDrinkEndTime.Text = settings.DrinkWaterEndTime;

            if (settings.DrinkWaterMode == DrinkWaterMode.Custom)
                RbModeCustom.IsChecked = true;
            else
                RbModeInterval.IsChecked = true;

            ListCustomDrinkTimes.ItemsSource = settings.CustomDrinkWaterTimes;
            
            // 待办事项
            ChkTodo.IsChecked = settings.TodoEnabled;
            ListTodo.ItemsSource = settings.TodoList;
            DpTodoDate.SelectedDate = DateTime.Today;
            
            // 久坐提醒
            ChkSedentaryReminder.IsChecked = settings.SedentaryReminderEnabled;
            TxtSedentaryInterval.Text = settings.SedentaryReminderIntervalMinutes.ToString();

            UpdateDrinkWaterUI();
            UpdateTodoUI();
            UpdateSedentaryUI();
            UpdateNotificationUI();
            TxtFocusModeAllowedApps.Text = settings.FocusModeAllowedApps ?? "";
            ChkFocusModeBluetooth.IsChecked = settings.FocusModeBluetoothEnabled;
            ChkFocusModeUsb.IsChecked = settings.FocusModeUsbEnabled;
            ChkFocusModeMessage.IsChecked = settings.FocusModeMessageEnabled;
            ChkFocusModeAllowAllApps.IsChecked = settings.FocusModeAllowAllApps;
            ChkFocusModeShowMediaPlayer.IsChecked = settings.FocusModeShowMediaPlayer;
            ChkFocusModeShowVisualizer.IsChecked = settings.FocusModeShowVisualizer;
            ChkFocusModeShowSystemStats.IsChecked = settings.FocusModeSystemStatsEnabled;
            ChkFocusModeShowDrinkWater.IsChecked = settings.FocusModeDrinkWaterEnabled;
            ChkFocusModeShowSedentary.IsChecked = settings.FocusModeSedentaryEnabled;
            ChkFocusModeShowTodo.IsChecked = settings.FocusModeTodoEnabled;
            UpdateFocusChart();
            _isLoading = false;
        }

        private void SaveSettings()
        {
            if (_isLoading) return;

            // 系统自启动
            if (ChkStartWithWindows.IsChecked == true) EnableStartup();
            else DisableStartup();

            var settings = AppSettings.Load();
            settings.SystemStatsEnabled = ChkSystemStats.IsChecked == true;
            settings.BluetoothNotificationEnabled = ChkBluetoothNotification.IsChecked == true;
            settings.UsbNotificationEnabled = ChkUsbNotification.IsChecked == true;
            settings.MessageNotificationEnabled = ChkMessageNotification.IsChecked == true;
            settings.NotificationAllowAllApps = ChkNotificationAllowAllApps.IsChecked == true;
            settings.NotificationAppWhitelist = TxtNotificationWhitelist.Text ?? "";
            settings.NotificationDebugLoggingEnabled = ChkNotificationDebugLogging.IsChecked == true;
            settings.ShowMediaPlayer = ChkShowMediaPlayer.IsChecked == true;
            settings.ShowVisualizer = ChkShowVisualizer.IsChecked == true;
            settings.IslandOpacity = Math.Clamp(SldIslandOpacity.Value, 0.2, 1.0);
            settings.StandbyWidth = Math.Clamp(SldIslandWidth.Value, 80, 200);
            settings.StandbyHeight = Math.Clamp(SldIslandHeight.Value, 25, 50);
            
            // 喝水提醒
            settings.DrinkWaterEnabled = ChkDrinkWater.IsChecked == true;
            if (int.TryParse(TxtDrinkWaterInterval.Text, out int interval))
            {
                settings.DrinkWaterIntervalMinutes = Math.Max(1, interval);
            }
            if (TimeSpan.TryParse(TxtDrinkStartTime.Text, out _)) settings.DrinkWaterStartTime = TxtDrinkStartTime.Text;
            if (TimeSpan.TryParse(TxtDrinkEndTime.Text, out _)) settings.DrinkWaterEndTime = TxtDrinkEndTime.Text;
            settings.DrinkWaterMode = RbModeCustom.IsChecked == true ? DrinkWaterMode.Custom : DrinkWaterMode.Interval;
            
            // 久坐提醒
            settings.SedentaryReminderEnabled = ChkSedentaryReminder.IsChecked == true;
            if (int.TryParse(TxtSedentaryInterval.Text, out int sedentaryInterval))
            {
                settings.SedentaryReminderIntervalMinutes = Math.Max(1, sedentaryInterval);
            }

            // 待办事项
            settings.TodoEnabled = ChkTodo.IsChecked == true;

            // 专注模式
            settings.FocusModeAllowedApps = TxtFocusModeAllowedApps.Text ?? "";
            settings.FocusModeBluetoothEnabled = ChkFocusModeBluetooth.IsChecked == true;
            settings.FocusModeUsbEnabled = ChkFocusModeUsb.IsChecked == true;
            settings.FocusModeMessageEnabled = ChkFocusModeMessage.IsChecked == true;
            settings.FocusModeAllowAllApps = ChkFocusModeAllowAllApps.IsChecked == true;
            settings.FocusModeShowMediaPlayer = ChkFocusModeShowMediaPlayer.IsChecked == true;
            settings.FocusModeShowVisualizer = ChkFocusModeShowVisualizer.IsChecked == true;
            settings.FocusModeSystemStatsEnabled = ChkFocusModeShowSystemStats.IsChecked == true;
            settings.FocusModeDrinkWaterEnabled = ChkFocusModeShowDrinkWater.IsChecked == true;
            settings.FocusModeSedentaryEnabled = ChkFocusModeShowSedentary.IsChecked == true;
            settings.FocusModeTodoEnabled = ChkFocusModeShowTodo.IsChecked == true;

            settings.Save();

            // 通知主窗口重新加载设置
            NotifyMainWindowReload();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void AutoSave_Changed(object sender, RoutedEventArgs e) 
        {
            UpdateNotificationUI();
            SaveSettings();
        }

        private void UpdateNotificationUI()
        {
             if (ChkNotificationAllowAllApps.IsChecked == true)
             {
                 TxtNotificationWhitelist.Visibility = Visibility.Collapsed;
                 TxtWhitelistLabel.Visibility = Visibility.Collapsed;
             }
             else
             {
                 TxtNotificationWhitelist.Visibility = Visibility.Visible;
                 TxtWhitelistLabel.Visibility = Visibility.Visible;
             }
        }
        
        private void ChkSedentary_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSedentaryUI();
            SaveSettings();
        }

        private void ChkSedentary_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateSedentaryUI();
            SaveSettings();
        }

        private void UpdateSedentaryUI()
        {
            if (PanelSedentarySettings == null) return;
            PanelSedentarySettings.Visibility = ChkSedentaryReminder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoSave_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string target) return;

            PanelNotify.Visibility = target == "PanelNotify" ? Visibility.Visible : Visibility.Collapsed;
            PanelMedia.Visibility = target == "PanelMedia" ? Visibility.Visible : Visibility.Collapsed;
            PanelAppearance.Visibility = target == "PanelAppearance" ? Visibility.Visible : Visibility.Collapsed;
            PanelSystem.Visibility = target == "PanelSystem" ? Visibility.Visible : Visibility.Collapsed;
            PanelHealth.Visibility = target == "PanelHealth" ? Visibility.Visible : Visibility.Collapsed;
            PanelTodo.Visibility = target == "PanelTodo" ? Visibility.Visible : Visibility.Collapsed;
            PanelFocus.Visibility = target == "PanelFocus" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnTestNotification_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.ShowTestNotification();
            }
        }

        private void SldIslandOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtIslandOpacityValue == null) return;
            TxtIslandOpacityValue.Text = $"{Math.Round(e.NewValue * 100):0}%";
            SaveSettings();
        }

        private void SldIslandWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtIslandWidthValue == null) return;
            TxtIslandWidthValue.Text = Math.Round(e.NewValue).ToString();
            SaveSettings();
        }

        private void SldIslandHeight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtIslandHeightValue == null) return;
            TxtIslandHeightValue.Text = Math.Round(e.NewValue).ToString();
            SaveSettings();
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("topx") != null;
            }
            catch { return false; }
        }

        private void EnableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("topx", System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法启用开机自启: {ex.Message}");
            }
        }

        private void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("topx", false);
            }
            catch { }
        }

        private void ChkDrinkWater_Checked(object sender, RoutedEventArgs e)
        {
            UpdateDrinkWaterUI();
            SaveSettings();
        }

        private void ChkDrinkWater_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateDrinkWaterUI();
            SaveSettings();
        }

        private void RbMode_Checked(object sender, RoutedEventArgs e)
        {
            UpdateDrinkWaterUI();
            SaveSettings();
        }

        private void UpdateDrinkWaterUI()
        {
            if (PanelDrinkWaterSettings == null) return;

            if (ChkDrinkWater.IsChecked == true)
            {
                PanelDrinkWaterSettings.Visibility = Visibility.Visible;
                if (RbModeCustom.IsChecked == true)
                {
                    PanelModeInterval.Visibility = Visibility.Collapsed;
                    PanelModeCustom.Visibility = Visibility.Visible;
                }
                else
                {
                    PanelModeInterval.Visibility = Visibility.Visible;
                    PanelModeCustom.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PanelDrinkWaterSettings.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkTodo_Checked(object sender, RoutedEventArgs e)
        {
            UpdateTodoUI();
            SaveSettings();
        }

        private void ChkTodo_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateTodoUI();
            SaveSettings();
        }

        private void UpdateTodoUI()
        {
            if (PanelTodoSettings == null) return;
            PanelTodoSettings.Visibility = ChkTodo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- 自定义时间逻辑 ---

        private void BtnAddCustomDrinkTime_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtCustomDrinkTime.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            if (!TryParseTime(input, out string formattedTime))
            {
                MessageBox.Show("时间格式错误，请使用 HH:MM");
                return;
            }

            // 重新加载以进行修改
            var currentList = (List<string>)ListCustomDrinkTimes.ItemsSource ?? new List<string>();
            if (!currentList.Contains(formattedTime))
            {
                currentList.Add(formattedTime);
                currentList.Sort();
                
                // 刷新列表
                ListCustomDrinkTimes.ItemsSource = null;
                ListCustomDrinkTimes.ItemsSource = currentList;
                
                // 立即保存
                var s = AppSettings.Load();
                s.CustomDrinkWaterTimes = currentList;
                s.Save();
                NotifyMainWindowReload();
            }
            
            TxtCustomDrinkTime.Text = "";
        }

        private void BtnDeleteCustomDrinkTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string timeStr)
            {
                var currentList = (List<string>)ListCustomDrinkTimes.ItemsSource;
                if (currentList != null && currentList.Remove(timeStr))
                {
                    ListCustomDrinkTimes.ItemsSource = null;
                    ListCustomDrinkTimes.ItemsSource = currentList;

                    var s = AppSettings.Load();
                    s.CustomDrinkWaterTimes = currentList;
                    s.Save();
                    NotifyMainWindowReload();
                }
            }
        }

        // --- 待办事项逻辑 ---

        private void BtnAddTodo_Click(object sender, RoutedEventArgs e)
        {
            if (DpTodoDate.SelectedDate == null) return;
            if (!TimeSpan.TryParse(TxtTodoTime.Text, out TimeSpan time)) return;
            if (string.IsNullOrWhiteSpace(TxtTodoContent.Text)) return;

            var newItem = new TodoItem
            {
                ReminderTime = DpTodoDate.SelectedDate.Value.Date + time,
                Content = TxtTodoContent.Text,
                IsCompleted = false
            };

            var currentList = (List<TodoItem>)ListTodo.ItemsSource ?? new List<TodoItem>();
            currentList.Add(newItem);
            
            // 按时间排序
            currentList.Sort((a, b) => a.ReminderTime.CompareTo(b.ReminderTime));

            ListTodo.ItemsSource = null;
            ListTodo.ItemsSource = currentList;

            // 立即保存
            var s = AppSettings.Load();
            s.TodoList = currentList;
            s.Save();
            NotifyMainWindowReload();

            TxtTodoContent.Text = "";
        }

        private void BtnDeleteTodo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TodoItem item)
            {
                var currentList = (List<TodoItem>)ListTodo.ItemsSource;
                if (currentList != null)
                {
                    // 移除匹配的项目
                    currentList.RemoveAll(x => x.Content == item.Content && x.ReminderTime == item.ReminderTime);
                    
                    ListTodo.ItemsSource = null;
                    ListTodo.ItemsSource = currentList;

                    var s = AppSettings.Load();
                    s.TodoList = currentList;
                    s.Save();
                    NotifyMainWindowReload();
                }
            }
        }

        // --- 辅助方法 ---

        private void NotifyMainWindowReload()
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.ReloadSettings();
            }
        }

        private bool TryParseTime(string input, out string formattedTime)
        {
            formattedTime = "";
            input = input.Replace(" ", "").Replace(":", "");
            
            // 支持 930 -> 09:30, 1400 -> 14:00
            if (input.Length == 3) input = "0" + input;
            
            if (input.Length == 4 && int.TryParse(input, out _))
            {
                string h = input.Substring(0, 2);
                string m = input.Substring(2, 2);
                if (int.Parse(h) < 24 && int.Parse(m) < 60)
                {
                    formattedTime = $"{h}:{m}";
                    return true;
                }
            }
            
            // 标准解析
            if (TimeSpan.TryParse(input, out TimeSpan ts))
            {
                formattedTime = ts.ToString(@"hh\:mm");
                return true;
            }

            return false;
        }
        private void UpdateFocusChart()
        {
            try
            {
                var settings = AppSettings.Load();
                var history = settings.FocusHistory ?? new List<FocusSession>();
                
                var today = DateTime.Today;
                var chartData = new List<FocusChartItem>();
                
                // 计算最近 7 天 (含今天)
                for (int i = 6; i >= 0; i--)
                {
                    var date = today.AddDays(-i);
                    var totalSeconds = history
                        .Where(s => s.StartTime.Date == date)
                        .Sum(s => s.DurationSeconds);
                    
                    // 假设 2 小时 (7200s) 为 100% 高度 (100)
                    double barHeight = Math.Min(100, (totalSeconds / 7200.0) * 100);
                    if (totalSeconds > 0 && barHeight < 5) barHeight = 5; // 最小可见高度

                    chartData.Add(new FocusChartItem
                    {
                        BarHeight = barHeight,
                        DayLabel = i == 0 ? "今天" : date.ToString("MM/dd"),
                        ToolTipText = $"{date:yyyy-MM-dd}\n时长: {FormatFocusDuration(totalSeconds)}"
                    });
                }
                
                ChartFocusHistory.ItemsSource = chartData;
            }
            catch { }
        }

        private string FormatFocusDuration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}小时 {t.Minutes}分";
            return $"{t.Minutes}分 {t.Seconds}秒";
        }
    }

    public class FocusChartItem
    {
        public double BarHeight { get; set; }
        public string DayLabel { get; set; }
        public string ToolTipText { get; set; }
    }
}
