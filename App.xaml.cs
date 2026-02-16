using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace TopX
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;

        private MainWindow? GetMainWindow()
        {
            return Current?.MainWindow as MainWindow;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                _trayIcon = new TaskbarIcon
                {
                    ToolTipText = "topx - 灵动岛",
                    ContextMenu = Resources["TrayMenu"] as ContextMenu,
                    IconSource = new BitmapImage(new Uri("pack://application:,,,/ico.ico"))
                };
                _trayIcon.TrayLeftMouseUp += (s, args) => Settings_Click(s, args);
            }
            catch
            {
                // Keep app running even if tray icon creation fails.
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _trayIcon?.Dispose();
            }
            catch { }
            finally
            {
                _trayIcon = null;
            }

            base.OnExit(e);
        }

        private void TrayMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && (mi.Tag as string) == "ToggleIsland")
                {
                    UpdateToggleMenuHeader(mi);
                    break;
                }
            }
        }

        private void ToggleIsland_Click(object sender, RoutedEventArgs e)
        {
            var window = GetMainWindow();
            if (window == null) return;

            if (window.IsVisible) window.HideIsland();
            else window.ShowIsland();

            if (sender is MenuItem mi) UpdateToggleMenuHeader(mi);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var owner = GetMainWindow();
            var settingsWindow = new SettingsWindow();
            if (owner != null) settingsWindow.Owner = owner;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Shutdown();
        }

        private void UpdateToggleMenuHeader(MenuItem item)
        {
            var window = GetMainWindow();
            item.Header = window != null && window.IsVisible ? "隐藏灵动岛" : "显示灵动岛";
        }
    }
}
