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
            ChkBluetoothNotification.IsChecked = settings.BluetoothNotificationEnabled;
            ChkUsbNotification.IsChecked = settings.UsbNotificationEnabled;
            ChkMessageNotification.IsChecked = settings.MessageNotificationEnabled;
            ChkShowMediaPlayer.IsChecked = settings.ShowMediaPlayer;
            ChkShowVisualizer.IsChecked = settings.ShowVisualizer;
            SldIslandOpacity.Value = Math.Clamp(settings.IslandOpacity, 0.2, 1.0);

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
            
            UpdateDrinkWaterUI();
            UpdateTodoUI();
            _isLoading = false;
        }

        private void SaveSettings()
        {
            if (_isLoading) return;

            // 系统自启动
            if (ChkStartWithWindows.IsChecked == true) EnableStartup();
            else DisableStartup();

            var settings = AppSettings.Load();
            settings.BluetoothNotificationEnabled = ChkBluetoothNotification.IsChecked == true;
            settings.UsbNotificationEnabled = ChkUsbNotification.IsChecked == true;
            settings.MessageNotificationEnabled = ChkMessageNotification.IsChecked == true;
            settings.ShowMediaPlayer = ChkShowMediaPlayer.IsChecked == true;
            settings.ShowVisualizer = ChkShowVisualizer.IsChecked == true;
            settings.IslandOpacity = Math.Clamp(SldIslandOpacity.Value, 0.2, 1.0);
            
            // 喝水提醒
            settings.DrinkWaterEnabled = ChkDrinkWater.IsChecked == true;
            if (int.TryParse(TxtDrinkWaterInterval.Text, out int interval))
            {
                settings.DrinkWaterIntervalMinutes = Math.Max(1, interval);
            }
            if (TimeSpan.TryParse(TxtDrinkStartTime.Text, out _)) settings.DrinkWaterStartTime = TxtDrinkStartTime.Text;
            if (TimeSpan.TryParse(TxtDrinkEndTime.Text, out _)) settings.DrinkWaterEndTime = TxtDrinkEndTime.Text;
            settings.DrinkWaterMode = RbModeCustom.IsChecked == true ? DrinkWaterMode.Custom : DrinkWaterMode.Interval;
            
            // 待办事项
            settings.TodoEnabled = ChkTodo.IsChecked == true;

            settings.Save();

            // 通知主窗口重新加载设置
            NotifyMainWindowReload();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void AutoSave_Changed(object sender, RoutedEventArgs e) => SaveSettings();

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
        }

        private void SldIslandOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtIslandOpacityValue == null) return;
            TxtIslandOpacityValue.Text = $"{Math.Round(e.NewValue * 100):0}%";
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
    }
}
