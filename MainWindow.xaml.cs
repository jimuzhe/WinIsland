using Microsoft.Data.Sqlite;
using NAudio.Wave;
using System;
using System.IO;
using System.Management; // WMI
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications.Management; // UserNotificationListener
using Windows.UI.Notifications; // UserNotification
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Application = System.Windows.Application;
using System.Windows.Media.Effects; // Screen
using System.Text.RegularExpressions;

namespace WinIsland
{
    public partial class MainWindow : Window
    {
        private bool _isFileStationActive = false;
        private bool _isFileDragHover = false;
        private List<string> _storedFiles = new List<string>();
        private Spring _widthSpring;
        private Spring _heightSpring;
        private DateTime _lastFrameTime;
        private AppSettings? _islandSettings;
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private WasapiLoopbackCapture? _capture;
        private float _currentVolume = 0;
        private bool _lyricUpdateInFlight = false;
        private DateTime _lastLyricPoll = DateTime.MinValue;
        private string _lastLyricText = "";
        private bool _isLyricVisible = false;
        private readonly TimeSpan _lyricPollInterval = TimeSpan.FromMilliseconds(300);
        private readonly TimeSpan _lyricTimelineOffset = TimeSpan.FromSeconds(1.7);
        private Dictionary<string, List<TrackInfo>> _tracksByTitle;
        private DateTime _trackIndexLastWriteUtc = DateTime.MinValue;
        private string _currentLyricTrackId;

        private List<LyricLine> _currentLyricLines;
        private HttpClient _httpClient;
        private Dictionary<string, List<LyricLine>> _fallbackLyricCache = new Dictionary<string, List<LyricLine>>();
        private string _currentSongTitle;
        private string _currentSongArtist;
        private string _currentSubtitle;

        // 通知相关
        private DispatcherTimer _notificationTimer;
        private bool _isNotificationActive = false;
        private UserNotificationListener _listener;
        private const double MinIslandOpacity = 0.2;
        private Window _centerGuideWindow;
        private const double CenterSnapThreshold = 12;
        private bool _isPlaying = false;
        private DispatcherTimer _playbackStatusTimer;
        private bool _manualStandby = false;
        private string _dismissedSessionId;
        private bool _isSwipeTracking = false;
        private bool _isSwiping = false;
        private bool _swipeTriggered = false;
        private Point _swipeStart;
        private readonly Dictionary<string, ImageSource> _audioSourceIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private const double SwipeStartThreshold = 8;
        private const double SwipeTriggerDistance = 60;
        private const double SwipeMaxVerticalDelta = 28;

        private AppSettings GetSettings()
        {
            _islandSettings ??= AppSettings.Load();
            return _islandSettings;
        }

        private double GetActiveOpacity()
        {
            double value = 1.0;
            try
            {
                value = GetSettings().IslandOpacity;
            }
            catch { value = 1.0; }

            if (double.IsNaN(value) || value <= 0) value = 1.0;
            return Math.Clamp(value, MinIslandOpacity, 1.0);
        }

        private double GetStandbyOpacity() => GetActiveOpacity();

        public MainWindow()
        {
            InitializeComponent();
            
            // 隐藏在 Alt+Tab 切换器中
            this.Loaded += (s, e) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
           
                  Task.Delay(100).ContinueWith(_ => Dispatcher.Invoke(() => SetClickThrough(false)));
            };
            
            // 窗口内容渲染完成后居中
            this.ContentRendered += (s, e) =>
            {
                CenterWindowAtTop();
            };
            
            InitializePhysics();
            InitializeMediaListener();
            InitializeAudioCapture();

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            InitializeDeviceWatcher();
            InitializeNotificationTimer();
            InitializeNotificationListener();
            InitializeDrinkWaterFeature();
            InitializeTodoFeature();
        }

        public void ReloadSettings()
        {
            _islandSettings = AppSettings.Load();
            InitializeDrinkWaterFeature();
            InitializeTodoFeature();
            ApplyIslandOpacityForCurrentState();
            CheckCurrentSession();
        }

        private void ApplyIslandOpacityForCurrentState()
        {
            if (DynamicIsland == null) return;

            bool isStandby = !_isNotificationActive &&
                             !_isFileStationActive &&
                             _storedFiles.Count == 0 &&
                             _currentSession == null;

            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = isStandby ? GetStandbyOpacity() : GetActiveOpacity();
        }

        private void CenterWindowAtTop()
        {
            try
            {
                // 获取主屏幕的工作区域
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var settings = AppSettings.Load();
                if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
                {
                    var left = settings.WindowLeft.Value;
                    var top = settings.WindowTop.Value;

                    var maxLeft = Math.Max(0, screenWidth - this.Width);
                    var maxTop = Math.Max(0, screenHeight - this.Height);
                    this.Left = Math.Max(0, Math.Min(left, maxLeft));
                    this.Top = Math.Max(0, Math.Min(top, maxTop));
                }
                else
                {
                    // 计算居中位置
                    this.Left = (screenWidth - this.Width) / 2;
                    this.Top = 0; // 允许贴到顶部
                }
                
                LogDebug($"Window centered: Left={this.Left}, Top={this.Top}, Width={this.Width}, ScreenWidth={screenWidth}");
            }
            catch (Exception ex)
            {
                LogDebug($"Center window error: {ex.Message}");
            }
        }

        // Win32 API 声明
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        private void SetClickThrough(bool enable)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (enable)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
                    LogDebug("Click-through enabled");
                }
                else
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
                    LogDebug("Click-through disabled");
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        #region 0. 通知逻辑 (WMI & UserNotificationListener)

        private void InitializeNotificationTimer()
        {
            _notificationTimer = new DispatcherTimer();
            _notificationTimer.Interval = TimeSpan.FromSeconds(3); // 通知显示3秒
            _notificationTimer.Tick += (s, e) => HideNotification();
        }

        private async void InitializeNotificationListener()
        {
            try
            {
                if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener")) return;

                _listener = UserNotificationListener.Current;
                var accessStatus = await _listener.RequestAccessAsync();

                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    _listener.NotificationChanged += Listener_NotificationChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification Listener Error: {ex.Message}");
            }
        }

        private void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                if (!GetSettings().MessageNotificationEnabled) return;

                // 暂时移除 ChangeType 检查
                var notifId = args.UserNotificationId;
                var notif = _listener.GetNotification(notifId);
                if (notif == null) return;

                var appName = notif.AppInfo.DisplayInfo.DisplayName;
                if (string.IsNullOrEmpty(appName)) return;

                // 简单的过滤逻辑: 微信 或 QQ
                bool isWeChat = appName.Contains("WeChat", StringComparison.OrdinalIgnoreCase) || appName.Contains("微信");
                bool isQQ = appName.Contains("QQ", StringComparison.OrdinalIgnoreCase);

                if (isWeChat || isQQ)
                {
                    string displayMsg = $"{appName}: New Message";

                    // 尝试获取详细内容
                    try
                    {
                        var binding = notif.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                        if (binding != null)
                        {
                            var texts = binding.GetTextElements();
                            string title = texts.Count > 0 ? texts[0].Text : appName;
                            string body = texts.Count > 1 ? texts[1].Text : "New Message";
                            displayMsg = $"{title}: {body}";
                        }
                    }
                    catch { }
                    
                    Dispatcher.Invoke(() => ShowMessageNotification(displayMsg));
                }
            }
            catch { }
        }

        // 使用 Windows.Devices.Enumeration 替代 WMI，更加轻量且实时
        private Windows.Devices.Enumeration.DeviceWatcher _bluetoothWatcher;
        private Windows.Devices.Enumeration.DeviceWatcher _usbWatcher;
        private System.Collections.Concurrent.ConcurrentDictionary<string, string> _deviceMap = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        private System.Collections.Concurrent.ConcurrentDictionary<string, (bool isConnected, DateTime lastUpdate)> _deviceStateCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool, DateTime)>();
        private bool _isBluetoothEnumComplete = false;
        private bool _isUsbEnumComplete = false;

        private void InitializeDeviceWatcher()
        {
            try
            {
                LogDebug("Initializing Device Watchers...");
                
                // 蓝牙设备监听 (配对的蓝牙设备)
                string bluetoothSelector = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
                
                // 请求额外属性，特别是连接状态
                var requestedProperties = new string[]
                {
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.SignalStrength",
                    "System.Devices.Aep.Bluetooth.Le.IsConnectable"
                };
                
                _bluetoothWatcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
                    bluetoothSelector,
                    requestedProperties,
                    Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);

                _bluetoothWatcher.Added += BluetoothWatcher_Added;
                _bluetoothWatcher.Removed += BluetoothWatcher_Removed;
                _bluetoothWatcher.Updated += BluetoothWatcher_Updated;
                _bluetoothWatcher.EnumerationCompleted += (s, e) => 
                { 
                    _isBluetoothEnumComplete = true;
                    LogDebug("Bluetooth enumeration completed");
                };
                _bluetoothWatcher.Start();
                LogDebug("Bluetooth watcher started");

                // USB 设备监听
                string usbSelector = "System.Devices.InterfaceClassGuid:=\"{a5dcbf10-6530-11d2-901f-00c04fb951ed}\""; // USB 设备接口
                _usbWatcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
                    usbSelector,
                    null,
                    Windows.Devices.Enumeration.DeviceInformationKind.DeviceInterface);

                _usbWatcher.Added += UsbWatcher_Added;
                _usbWatcher.Removed += UsbWatcher_Removed;
                _usbWatcher.EnumerationCompleted += (s, e) => { _isUsbEnumComplete = true; };
                _usbWatcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Device Watcher Error: {ex.Message}");
            }
        }

        private void BluetoothWatcher_Added(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformation args)
        {
            if (!GetSettings().BluetoothNotificationEnabled) return;

            LogDebug($"BT Added: {args.Name} (ID: {args.Id.Substring(0, Math.Min(30, args.Id.Length))})");
            
            // 过滤无效设备名
            if (string.IsNullOrEmpty(args.Name) || !IsValidDeviceName(args.Name))
            {
                LogDebug($"BT Added: Filtered invalid name: {args.Name}");
                return;
            }
            
            _deviceMap.TryAdd(args.Id, args.Name);
            
            // 初始化设备状态缓存（假设初始为断开）
            if (args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                _deviceStateCache[args.Id] = (isConnected, DateTime.Now);
                LogDebug($"BT Added: Initial state = {isConnected}");
            }

            // 只在枚举完成后且设备是连接状态才显示通知
            if (_isBluetoothEnumComplete && args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                if (isConnected)
                {
                    LogDebug($"BT Added Notification: {args.Name}");
                    Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {args.Name}", true));
                }
            }
        }

        private void BluetoothWatcher_Removed(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            if (!GetSettings().BluetoothNotificationEnabled) return;

            LogDebug($"BT Removed: ID={args.Id.Substring(0, Math.Min(30, args.Id.Length))}");
            
            // 检查该设备之前的连接状态
            bool wasConnected = false;
            if (_deviceStateCache.TryRemove(args.Id, out var lastState))
            {
                wasConnected = lastState.isConnected;
            }

            if (_deviceMap.TryRemove(args.Id, out string deviceName))
            {
                LogDebug($"BT Removed from map: {deviceName}");
                // 只有当枚举完成，且设备之前确实是连接状态时，才显示断开通知
                // 这样避免了未连接的配对设备在系统后台刷新时触发误报
                if (_isBluetoothEnumComplete && !string.IsNullOrEmpty(deviceName) && wasConnected)
                {
                    LogDebug($"BT Removed Notification: {deviceName}");
                    Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {deviceName}", false));
                }
            }
        }

        private void BluetoothWatcher_Updated(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            if (!GetSettings().BluetoothNotificationEnabled) return;

            // 蓝牙设备状态更新（连接/断开）
            LogDebug($"BT Updated: ID={args.Id.Substring(0, Math.Min(30, args.Id.Length))}, Props={args.Properties.Count}");
            
            if (args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                LogDebug($"BT IsConnected: {isConnected}");
                
                if (_deviceMap.TryGetValue(args.Id, out string deviceName) && !string.IsNullOrEmpty(deviceName))
                {
                    // 过滤无效设备名
                    if (!IsValidDeviceName(deviceName))
                    {
                        LogDebug($"BT Updated: Filtered invalid name: {deviceName}");
                        return;
                    }
                    
                    // 防抖：检查状态是否真的改变
                    var now = DateTime.Now;
                    bool shouldNotify = false;
                    
                    if (_deviceStateCache.TryGetValue(args.Id, out var cachedState))
                    {
                        // 状态没变，忽略
                        if (cachedState.isConnected == isConnected)
                        {
                            LogDebug($"BT Updated: State unchanged, ignored");
                            return;
                        }
                        
                        // 距离上次更新太近（2秒内），忽略
                        if ((now - cachedState.lastUpdate).TotalSeconds < 2)
                        {
                            LogDebug($"BT Updated: Too frequent, ignored (last: {(now - cachedState.lastUpdate).TotalSeconds:F1}s ago)");
                            return;
                        }
                        
                        // 状态真的改变了，且时间间隔足够
                        shouldNotify = true;
                    }
                    else
                    {
                        // 第一次收到这个设备的状态，只在连接时通知
                        shouldNotify = isConnected;
                    }
                    
                    // 更新缓存
                    _deviceStateCache[args.Id] = (isConnected, now);
                    
                    if (shouldNotify)
                    {
                        LogDebug($"BT Updated Notification: {deviceName} -> {(isConnected ? "Connected" : "Disconnected")}");
                        Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {deviceName}", isConnected));
                    }
                }
                else
                {
                    LogDebug($"BT device not in map or empty name");
                }
            }
        }

        private bool IsValidDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            
            // 过滤太短的名字（可能是 MAC 地址片段）
            if (name.Length < 4) return false;
            
            // 过滤纯数字或纯字母+数字组合（如 A077, 1234）
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z0-9]{4,6}$"))
            {
                LogDebug($"Filtered MAC-like name: {name}");
                return false;
            }
            
            // 过滤包含特殊字符的设备ID
            if (name.Contains("\\") || name.Contains("{") || name.Contains("}"))
            {
                return false;
            }
            
            return true;
        }

        private void LogDebug(string message)
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");
                string logMessage = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss}: {message}\n";
                File.AppendAllText(logPath, logMessage);
            }
            catch { }
        }

        private void UsbWatcher_Added(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformation args)
        {
            if (!GetSettings().UsbNotificationEnabled) return;

            _deviceMap.TryAdd(args.Id, args.Name);

            if (_isUsbEnumComplete && !string.IsNullOrEmpty(args.Name))
            {
                Dispatcher.Invoke(() => ShowDeviceNotification($"USB: {args.Name}", true));
            }
        }

        private void UsbWatcher_Removed(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            if (!GetSettings().UsbNotificationEnabled) return;

            if (_deviceMap.TryRemove(args.Id, out string deviceName))
            {
                if (_isUsbEnumComplete && !string.IsNullOrEmpty(deviceName))
                {
                    Dispatcher.Invoke(() => ShowDeviceNotification($"USB: {deviceName}", false));
                }
            }
        }

        private void ShowDeviceNotification(string deviceName, bool isConnected)
        {
            if (!GetSettings().BluetoothNotificationEnabled && !GetSettings().UsbNotificationEnabled) return;

            ActivateNotification();
                    

            NotificationText.Text = deviceName;
            
            if (isConnected)
            {
                IconConnect.Visibility = Visibility.Visible;
                IconDisconnect.Visibility = Visibility.Collapsed;
                IconMessage.Visibility = Visibility.Collapsed;
                NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 204)); // Green
            }
            else
            {
                IconConnect.Visibility = Visibility.Collapsed;
                IconDisconnect.Visibility = Visibility.Visible;
                IconMessage.Visibility = Visibility.Collapsed;
                NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 51, 51)); // Red
            }

            PlayFlipAnimation();
        }

        private void ShowMessageNotification(string message)
        {
            if (!GetSettings().MessageNotificationEnabled) return;

            ActivateNotification();

            NotificationText.Text = message;
            
            IconConnect.Visibility = Visibility.Collapsed;
            IconDisconnect.Visibility = Visibility.Collapsed;
            IconMessage.Visibility = Visibility.Visible;
            NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(0, 191, 255)); // DeepSkyBlue

            PlayFlipAnimation();
        }

        private void ActivateNotification()
        {
            _isNotificationActive = true;
            _notificationTimer.Stop();
            _notificationTimer.Start();

            HideAllMediaElements();

            // 显示通知面板
            NotificationPanel.Visibility = Visibility.Visible;
            NotificationPanel.Opacity = 0;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;
            FileStationPanel.Visibility = Visibility.Collapsed;

            DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
            SetClickThrough(false); // 激活通知时允许交互
            
            // 清除动画锁定，确保 1.0 生效
            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = GetActiveOpacity();

            // 设定通知尺寸
            _widthSpring.Target = 320;
            _heightSpring.Target = 50;

            // 内容淡入动画
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NotificationPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void PlayFlipAnimation()
        {
            // 3D 翻转效果动画
            var flipAnimation = new DoubleAnimationUsingKeyFrames();
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
            });
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            var scaleYAnimation = new DoubleAnimationUsingKeyFrames();
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            NotificationIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnimation);
            NotificationIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
        }

        private void HideNotification()
        {
            _isNotificationActive = false;
            _notificationTimer.Stop();

            // 淡出动画
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Collapsed;
                
                // 恢复之前的状态
                CheckCurrentSession();
            };

            if (NotificationPanel.Visibility == Visibility.Visible)
                NotificationPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else if (DrinkWaterPanel.Visibility == Visibility.Visible)
                DrinkWaterPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else if (TodoPanel.Visibility == Visibility.Visible)
                TodoPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else
            {
                // 如果没有可见的面板，直接恢复状态
                CheckCurrentSession();
            }
        }

        #endregion

        #region Drink Water Notification
        
        private DispatcherTimer? _drinkWaterScheduler;
        private DateTime _nextDrinkTime;

        private void InitializeDrinkWaterFeature()
        {
            _islandSettings = AppSettings.Load();
            
            if (_drinkWaterScheduler == null)
            {
                _drinkWaterScheduler = new DispatcherTimer();
                _drinkWaterScheduler.Interval = TimeSpan.FromSeconds(30); // 每30秒检查一次
                _drinkWaterScheduler.Tick += DrinkWaterScheduler_Tick;
            }

            if (_islandSettings?.DrinkWaterEnabled == true)
            {
                // 如果是首次启用或重新启用，设置下一次提醒时间
                if (!_drinkWaterScheduler.IsEnabled)
                {
                    ResetNextDrinkTime();
                    _drinkWaterScheduler.Start();
                }
            }
            else
            {
                _drinkWaterScheduler.Stop();
            }
        }

        private void ResetNextDrinkTime()
        {
            var intervalMinutes = _islandSettings?.DrinkWaterIntervalMinutes ?? 30;
            _nextDrinkTime = DateTime.Now.AddMinutes(intervalMinutes);
            LogDebug($"Next drink time set to: {_nextDrinkTime}");
        }

        private string _lastTriggeredCustomTime = "";

                private void DrinkWaterScheduler_Tick(object sender, EventArgs e)
        {
            if (_islandSettings == null || !_islandSettings.DrinkWaterEnabled) return;

            if (_islandSettings.DrinkWaterMode == DrinkWaterMode.Interval)
            {
                if (DateTime.Now >= _nextDrinkTime && IsWithinActiveHours())
                {
                    ShowDrinkWaterNotification();
                    ResetNextDrinkTime();
                }
            }
            else // 自定义模式
            {
                var nowStr = DateTime.Now.ToString("HH:mm");
                if (_islandSettings.CustomDrinkWaterTimes != null && _islandSettings.CustomDrinkWaterTimes.Contains(nowStr))
                {
                    if (_lastTriggeredCustomTime != nowStr)
                    {
                        ShowDrinkWaterNotification();
                        _lastTriggeredCustomTime = nowStr;
                    }
                }
            }
        }

        private bool IsWithinActiveHours()
        {
            if (_islandSettings == null) return true;
            try
            {
                if (TimeSpan.TryParse(_islandSettings.DrinkWaterStartTime, out TimeSpan start) &&
                    TimeSpan.TryParse(_islandSettings.DrinkWaterEndTime, out TimeSpan end))
                {
                    var now = DateTime.Now.TimeOfDay;
                    if (start <= end)
                    {
                        return now >= start && now <= end;
                    }
                    else
                    {
                        // 跨午夜 (例如 22:00 到 06:00)
                        return now >= start || now <= end;
                    }
                }
            }
            catch { }
            return true; // 如果解析失败默认为 true
        }

        private void ShowDrinkWaterNotification()
        {
            Dispatcher.Invoke(() =>
            {
                _isNotificationActive = true;
                _notificationTimer.Stop(); // 停止其他通知的自动隐藏计时器

                HideAllMediaElements();

                // 显示喝水提醒
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Visible;
                DrinkWaterPanel.Opacity = 0;
                TodoPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;

                DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
                SetClickThrough(false);
                
                // 清除动画锁定
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = GetActiveOpacity();

                // 展开动画 (更宽一点)
                _widthSpring.Target = 280;
                _heightSpring.Target = 50;

                // 岛屿发光脉冲 (蓝色)
                PlayIslandGlowEffect(Colors.DeepSkyBlue);

                // 内容进场 (上浮淡入)
                PlayContentEntranceAnimation(DrinkWaterPanel);

                // 水滴动画 (优化版)
                PlayWaterDropAnimation();
            });
        }

        private void PlayWaterDropAnimation()
        {
            // 水滴悬浮动画
            var floatAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))) 
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))) 
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

            WaterIconTranslate?.BeginAnimation(TranslateTransform.YProperty, floatAnim);
        }

        private void BtnDrank_Click(object sender, RoutedEventArgs e)
        {
            // 用户已确认
            HideNotification(); // 复用隐藏逻辑来重置状态
            ResetNextDrinkTime(); // 重置周期
        }

        #endregion

        #region Todo Notification

        private DispatcherTimer _todoScheduler;
        private TodoItem _currentTodoItem;

        private void InitializeTodoFeature()
        {
            _islandSettings = AppSettings.Load(); // 确保是最新的设置
            
            if (_todoScheduler == null)
            {
                _todoScheduler = new DispatcherTimer();
                _todoScheduler.Interval = TimeSpan.FromSeconds(15); // 每15秒检查一次
                _todoScheduler.Tick += TodoScheduler_Tick;
            }

            if (_islandSettings?.TodoEnabled == true) // Use null-conditional operator
            {
                if (!_todoScheduler.IsEnabled) _todoScheduler.Start();
            }
            else
            {
                _todoScheduler.Stop();
            }
        }

        private void TodoScheduler_Tick(object sender, EventArgs e)
        {
            if (_islandSettings == null || !_islandSettings.TodoEnabled || _islandSettings.TodoList == null) return;

            var now = DateTime.Now;
            
            foreach (var item in _islandSettings.TodoList)
            {
                if (!item.IsCompleted && item.ReminderTime <= now)
                {
                    // 找到了！
                    _currentTodoItem = item;
                    ShowTodoNotification(item);
                    
                    if (_isNotificationActive && TxtTodoMessage.Text == item.Content) return;
                    
                    break; // 一次只显示一个
                }
            }
        }

        private void ShowTodoNotification(TodoItem item)
        {
            Dispatcher.Invoke(() =>
            {
                _isNotificationActive = true;
                _notificationTimer.Stop();

                HideAllMediaElements();

                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;

                TodoPanel.Visibility = Visibility.Visible;
                TodoPanel.Opacity = 0;
                TxtTodoMessage.Text = item.Content;

                DynamicIsland.IsHitTestVisible = true; 
                SetClickThrough(false);
                
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = GetActiveOpacity();

                // 展开动画 (稍微更宽)
                _widthSpring.Target = 320;
                _heightSpring.Target = 50;

                // 岛屿发光脉冲 (橙色)
                PlayIslandGlowEffect(Colors.Orange);

                // 内容进场
                PlayContentEntranceAnimation(TodoPanel);

                // 图标动画
                PlayTodoIconAnimation();
            });
        }

        private void PlayTodoIconAnimation()
        {
            var rotateAnim = new DoubleAnimationUsingKeyFrames{ RepeatBehavior = RepeatBehavior.Forever };
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));

            TodoIconRotate?.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
        }

        private void PlayContentEntranceAnimation(FrameworkElement element)
        {
            // 确保 RenderTransform 准备就绪
            TranslateTransform translate = null;
            if (element.RenderTransform is TranslateTransform tt)
            {
                translate = tt;
            }
            else if (element.RenderTransform is TransformGroup tg)
            {
                foreach(var t in tg.Children) if(t is TranslateTransform) translate = t as TranslateTransform;
            }
            
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            // 重置状态
            translate.Y = 20;
            element.Opacity = 0;

            // 淡入
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            // 上浮
            var slideUp = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };

            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        private void PlayIslandGlowEffect(Color glowColor)
        {
            if (DynamicIsland.Effect is DropShadowEffect shadow)
            {
                var colorAnim = new ColorAnimation(glowColor, TimeSpan.FromMilliseconds(300))
                {
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(2),
                    FillBehavior = FillBehavior.Stop
                };

                // 稍微扩散一下 Shadow
                var blurAnim = new DoubleAnimation(shadow.BlurRadius, 30, TimeSpan.FromMilliseconds(300))
                {
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(2),
                    FillBehavior = FillBehavior.Stop
                };

                shadow.BeginAnimation(DropShadowEffect.ColorProperty, colorAnim);
                shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            }
        }

        private void BtnTodoDone_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTodoItem != null)
            {
                _currentTodoItem.IsCompleted = true;
                _islandSettings ??= AppSettings.Load();
                _islandSettings.Save();
                _currentTodoItem = null;
            }
            
            HideNotification();
        }

        #endregion

        #region 1. 按钮控制逻辑

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipPreviousAsync(); } catch { CheckCurrentSession(); }
        }

        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TryTogglePlayPauseAsync(); } catch { CheckCurrentSession(); }
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipNextAsync(); } catch { CheckCurrentSession(); }
        }

        private async void MediaProgressBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentSession == null) return;
            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                var total = timeline.EndTime.TotalSeconds;
                if (total > 0)
                {
                    var slider = (Slider)sender;
                    var targetSeconds = (slider.Value / 100.0) * total;
                    await _currentSession.TryChangePlaybackPositionAsync((long)TimeSpan.FromSeconds(targetSeconds).Ticks);
                }
            }
            catch (Exception ex) { LogDebug($"Seek Error: {ex.Message}"); }
        }

        #endregion

        #region 3. 媒体信息
        private async void InitializeMediaListener()
        {
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mediaManager.CurrentSessionChanged += (s, e) => CheckCurrentSession();
                InitializePlaybackStatusTimer();
                CheckCurrentSession();
            }
            catch { }
        }

        private void InitializePlaybackStatusTimer()
        {
            if (_playbackStatusTimer != null) return;

            _playbackStatusTimer = new DispatcherTimer();
            _playbackStatusTimer.Interval = TimeSpan.FromMilliseconds(500);
            _playbackStatusTimer.Tick += (s, e) =>
            {
                try
                {
                    if (_mediaManager == null) return;
                    var session = GetPreferredSession();
                    if (session == null)
                    {
                        if (!IsMediaSuppressed())
                        {
                            EnterStandbyMode();
                        }
                        return;
                    }

                    if (ShouldSuppressMediaForManualStandby(session))
                    {
                        EnterStandbyMode();
                        return;
                    }

                    if (_currentSession == null || session.SourceAppUserModelId != _currentSession.SourceAppUserModelId)
                    {
                        _currentSession = session;
                        ResetLyricState();
                        _currentSession.MediaPropertiesChanged += async (sender, args) => await UpdateMediaInfo(sender);
                        _currentSession.PlaybackInfoChanged += (sender, args) => UpdatePlaybackStatus(sender);
                        _ = UpdateMediaInfo(_currentSession);
                    }

                    UpdatePlaybackStatus(session);
                    UpdateMediaTimeline();
                    _ = TryUpdateLyricLineAsync(session);
                }
                catch { }
            };
            _playbackStatusTimer.Start();
        }

        private GlobalSystemMediaTransportControlsSession GetPreferredSession()
        {
            if (_mediaManager == null) return null;

            try
            {
                var sessions = _mediaManager.GetSessions();
                if (sessions == null || sessions.Count == 0)
                {
                    return _mediaManager.GetCurrentSession();
                }

                GlobalSystemMediaTransportControlsSession pausedCandidate = null;
                GlobalSystemMediaTransportControlsSession stoppedCandidate = null;
                GlobalSystemMediaTransportControlsSession firstCandidate = null;
                GlobalSystemMediaTransportControlsSession dismissedCandidate = null;
                var dismissedId = _manualStandby ? _dismissedSessionId : null;

                foreach (var s in sessions)
                {
                    if (s == null) continue;
                    var appId = s.SourceAppUserModelId;
                    if (!string.IsNullOrWhiteSpace(dismissedId) && appId == dismissedId)
                    {
                        dismissedCandidate ??= s;
                        continue;
                    }

                    if (firstCandidate == null) firstCandidate = s;
                    var info = s.GetPlaybackInfo();
                    var status = info?.PlaybackStatus;
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return s;
                    }
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused && pausedCandidate == null)
                    {
                        pausedCandidate = s;
                    }
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped && stoppedCandidate == null)
                    {
                        stoppedCandidate = s;
                    }
                }

                if (pausedCandidate != null) return pausedCandidate;
                if (stoppedCandidate != null) return stoppedCandidate;
                if (firstCandidate != null) return firstCandidate;
                if (dismissedCandidate != null) return dismissedCandidate;
                return _mediaManager.GetCurrentSession();
            }
            catch
            {
                return _mediaManager.GetCurrentSession();
            }
        }

        private void CheckCurrentSession()
        {
            // 如果正在显示通知，不要打断它，等通知结束后会自动调用此方法
            if (_isNotificationActive) return;

            // 如果正在进行文件拖放或有文件存储，不要打断
            if (_isFileStationActive) return;
            
            // 如果文件中转站有文件，优先显示中转站
            if (_storedFiles.Count > 0) 
            {
                ShowFileStationState();
                return;
            }

            if (_manualStandby)
            {
                try
                {
                    if (ShouldSuppressMediaForManualStandby(GetPreferredSession()))
                    {
                        EnterStandbyMode();
                        return;
                    }
                }
                catch { }
            }

            try
            {
                var session = GetPreferredSession();
                if (session != null)
                {
                    // 仅当 Session 真正变化时才重新绑定
                    if (_currentSession == null || _currentSession.SourceAppUserModelId != session.SourceAppUserModelId)
                    {
                        _currentSession = session;
                        ResetLyricState();
                        _currentSession.MediaPropertiesChanged += async (s, e) => await UpdateMediaInfo(s);
                        _currentSession.PlaybackInfoChanged += (s, e) => UpdatePlaybackStatus(s);
                    }
                    
                    _ = UpdateMediaInfo(_currentSession);
                    UpdatePlaybackStatus(_currentSession);
                }
                else
                {
                    EnterStandbyMode();
                }
            }
            catch
            {
                EnterStandbyMode();
            }
        }

        private void EnterStandbyMode()
        {
            _currentSession = null;
            _widthSpring.Target = 120;
            _heightSpring.Target = 35;
            
            Dispatcher.Invoke(() =>
            {
                HideAllMediaElements();
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;
                
                // 启用交互以支持文件拖放
                DynamicIsland.IsHitTestVisible = true;
                SetClickThrough(false); 
                
                // 清除可能存在的动画锁定，确保透明度设置生效
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = GetStandbyOpacity(); // 待机透明度
            });
           
        }

        private void ApplyMediaLayout(bool showMedia, bool hasLyric)
        {
            var oldTargetW = _widthSpring.Target;
            var oldTargetH = _heightSpring.Target;
            var settings = GetSettings();
            var reduceWidth = settings.ShowVisualizer ? 0 : 40;

            if (showMedia)
            {
                if (_isExpanded)
                {
                    _widthSpring.Target = 360;
                    _heightSpring.Target = 112;
                }
                else
                {
                    var baseWidth = hasLyric ? 260 : 200;
                    _widthSpring.Target = Math.Max(160, baseWidth - reduceWidth);
                    _heightSpring.Target = 48;
                }
            }
            else
            {
                _widthSpring.Target = 200;
                _heightSpring.Target = 45;
            }

            if (Math.Abs(oldTargetW - _widthSpring.Target) > 1 ||
                Math.Abs(oldTargetH - _heightSpring.Target) > 1)
            {
                _lastFrameTime = DateTime.Now;
            }
        }

        private void ResetLyricState()
        {
            _lastLyricText = "";
            _isLyricVisible = false;
            _lastLyricPoll = DateTime.MinValue;
            _currentLyricTrackId = null;
            _currentLyricLines = null;
            _currentSongTitle = null;
            _currentSongArtist = null;
            _currentSubtitle = null;

            if (SongLyricFull == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ResetLyricState);
                return;
            }

            SongLyricFull.Text = "";
            SongLyricFull.Visibility = Visibility.Collapsed;
            if (SongLyricPreview != null)
            {
                SongLyricPreview.Text = "";
                SongLyricPreview.Visibility = Visibility.Collapsed;
            }
            if (SongTitle != null) SongTitle.Visibility = Visibility.Visible;
        }

        private void RefreshCompactMediaText()
        {
            if (SongLyricPreview == null || SongTitle == null) return;

            var compactText = _isLyricVisible && !string.IsNullOrWhiteSpace(_lastLyricText)
                ? _lastLyricText
                : (string.IsNullOrWhiteSpace(_currentSongTitle) ? "Unknown Title" : _currentSongTitle);

            SongLyricPreview.Text = compactText;

            if (_isExpanded)
            {
                SongLyricPreview.Visibility = Visibility.Collapsed;
                SongTitle.Visibility = Visibility.Visible;
            }
            else
            {
                SongLyricPreview.Visibility = Visibility.Visible;
                SongTitle.Visibility = Visibility.Collapsed;
            }

            UpdateCompactVerticalAlignment();
        }

        private void UpdateCompactVerticalAlignment()
        {
            if (MainMediaArea == null || SongLyricPreview == null) return;

            if (_isExpanded)
            {
                MainMediaArea.VerticalAlignment = VerticalAlignment.Top;
                return;
            }

            // Compact mode: center when the displayed compact text is a single line
            // (lyric or title fallback), otherwise keep top alignment.
            if (string.IsNullOrWhiteSpace(SongLyricPreview.Text))
            {
                MainMediaArea.VerticalAlignment = VerticalAlignment.Top;
                return;
            }

            var lines = EstimateTextLineCount(SongLyricPreview.Text);
            MainMediaArea.VerticalAlignment = lines <= 1 ? VerticalAlignment.Center : VerticalAlignment.Top;
        }

        private int EstimateTextLineCount(string text)
        {
            if (string.IsNullOrEmpty(text) || SongLyricPreview == null) return 1;
            if (text.Contains('\n')) return 2;

            try
            {
                var width = Math.Max(1, MainMediaArea?.ActualWidth > 1 ? MainMediaArea.ActualWidth : 220);
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var formatted = new FormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        SongLyricPreview.FontFamily,
                        SongLyricPreview.FontStyle,
                        SongLyricPreview.FontWeight,
                        SongLyricPreview.FontStretch),
                    SongLyricPreview.FontSize,
                    System.Windows.Media.Brushes.White,
                    dpi)
                {
                    MaxTextWidth = width
                };

                var textWidth = Math.Max(1.0, formatted.WidthIncludingTrailingWhitespace);
                var lines = (int)Math.Ceiling(textWidth / width);
                return Math.Max(1, lines);
            }
            catch
            {
                return 1;
            }
        }

        private void UpdateAudioSourceBadgePosition(double coverSize, Thickness coverMargin)
        {
            if (AudioSourceBadge == null || AudioSourceBadgeIcon == null || AudioSourceBadgeText == null) return;

            var badgeSize = coverSize >= 80 ? 24 : 18; // 稍微调大一点 (原 20 : 16)
            AudioSourceBadge.Width = badgeSize;
            AudioSourceBadge.Height = badgeSize;
            AudioSourceBadge.CornerRadius = new CornerRadius(badgeSize / 2);
            
            // 动态调整内部图标和文字大小
            AudioSourceBadgeIcon.Width = badgeSize - 4;
            AudioSourceBadgeIcon.Height = badgeSize - 4;
            AudioSourceBadgeText.FontSize = badgeSize * 0.65;

            AudioSourceBadge.Margin = new Thickness(
                coverMargin.Left + coverSize - (badgeSize * 0.6), // 调整偏移位置
                coverMargin.Top + coverSize - (badgeSize * 0.6),
                0,
                0);
        }

        private void UpdateAudioSourceBadgeVisual(string sourceAppId)
        {
            if (AudioSourceBadgeText == null || AudioSourceBadgeIcon == null) return;

            var id = (sourceAppId ?? "").ToLowerInvariant();
            var glyph = "♪";

            if (id.Contains("spotify")) glyph = "S";
            else if (id.Contains("cloudmusic") || id.Contains("netease")) glyph = "N";
            else if (id.Contains("qqmusic") || id.Contains("tencent")) glyph = "Q";
            else if (id.Contains("zunemusic") || id.Contains("music.ui")) glyph = "W";
            else if (id.Contains("kugou")) glyph = "K";
            else if (id.Contains("kuwo") || id.Contains("kwp")) glyph = "K";
            else if (id.Contains("chrome")) glyph = "C";
            else if (id.Contains("edge")) glyph = "E";
            else if (id.Contains("firefox")) glyph = "F";
            else if (id.Contains("yesplay")) glyph = "Y";
            else
            {
                var name = GetProcessNameFromSourceId(sourceAppId);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var c = name.Trim()[0];
                    if (char.IsLetter(c)) glyph = char.ToUpperInvariant(c).ToString();
                }
            }

            AudioSourceBadgeText.Text = glyph;

            if (TryResolveSourceIcon(sourceAppId, out var iconSource))
            {
                AudioSourceBadgeIcon.Source = iconSource;
                AudioSourceBadgeIcon.Visibility = Visibility.Visible;
                AudioSourceBadgeText.Visibility = Visibility.Collapsed;
            }
            else
            {
                AudioSourceBadgeIcon.Source = null;
                AudioSourceBadgeIcon.Visibility = Visibility.Collapsed;
                AudioSourceBadgeText.Visibility = Visibility.Visible;
            }
        }

        private bool TryResolveSourceIcon(string sourceAppId, out ImageSource iconSource)
        {
            iconSource = null;
            if (string.IsNullOrWhiteSpace(sourceAppId)) return false;

            if (_audioSourceIconCache.TryGetValue(sourceAppId, out var cached))
            {
                iconSource = cached;
                return true;
            }

            var processName = GetProcessNameFromSourceId(sourceAppId);
            if (string.IsNullOrWhiteSpace(processName)) return false;

            try
            {
                // Try to find the process and its icon
                var processes = Process.GetProcessesByName(processName);
                string path = null;

                foreach (var p in processes)
                {
                    try
                    {
                        path = p.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) break;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                // If process lookup failed, try to find the application path from registry for common apps
                if (string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                        if (key != null)
                        {
                            path = key.GetValue("")?.ToString();
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        var image = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(32, 32)); // Get a slightly higher res icon
                        image.Freeze();

                        _audioSourceIconCache[sourceAppId] = image;
                        iconSource = image;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static string GetProcessNameFromSourceId(string sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId)) return "";

            // Win32 style usually "xxx.exe"
            if (sourceAppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFileNameWithoutExtension(sourceAppId);
            }

            // UWP style usually "PackageFamily!App"
            var bangIdx = sourceAppId.IndexOf('!');
            if (bangIdx > 0)
            {
                var family = sourceAppId[..bangIdx];
                var app = sourceAppId[(bangIdx + 1)..];
                if (!string.IsNullOrWhiteSpace(app))
                {
                    return app;
                }
                var token = family.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }

            // generic fallback
            var fallback = sourceAppId.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return fallback ?? "";
        }

        private void RestoreCompactMediaLayout()
        {
            SongLyricFull.Visibility = Visibility.Collapsed;
            SongLyricFull.Opacity = 0;

            ControlPanel.Visibility = Visibility.Collapsed;
            ControlPanel.Opacity = 0;
            ProgressArea.Visibility = Visibility.Collapsed;
            ProgressArea.Opacity = 0;
            TxtCurrentTime.Visibility = Visibility.Collapsed;
            TxtTotalTime.Visibility = Visibility.Collapsed;
            ArtistName.Visibility = Visibility.Collapsed;
            ArtistName.Opacity = 0;

            AlbumClip.Rect = new Rect(0, 0, 40, 40);
            AlbumCover.Width = 40;
            AlbumCover.Height = 40;
            AlbumCover.Margin = new Thickness(10, 4, 0, 0);
            UpdateAudioSourceBadgePosition(40, AlbumCover.Margin);
            MainMediaArea.MaxHeight = 40;
            MainMediaArea.Margin = new Thickness(12, 0, 14, 0);
            AudioSourceBadge.Visibility = Visibility.Collapsed;
            UpdateCompactVerticalAlignment();

            var settings = GetSettings();
            VisualizerContainer.Visibility = (settings.ShowVisualizer && AlbumCover.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshCompactMediaText();
        }

        private bool IsMediaSuppressed()
        {
            return _isNotificationActive || _isFileStationActive || _storedFiles.Count > 0;
        }

        private void HideAllMediaElements()
        {
            AlbumCover.Visibility = Visibility.Collapsed;
            AudioSourceBadge.Visibility = Visibility.Collapsed;
            SongTitle.Visibility = Visibility.Collapsed;
            ArtistName.Visibility = Visibility.Collapsed;
            SongLyricPreview.Visibility = Visibility.Collapsed;
            SongLyricFull.Visibility = Visibility.Collapsed;
            ProgressArea.Visibility = Visibility.Collapsed;
            ControlPanel.Visibility = Visibility.Collapsed;
            VisualizerContainer.Visibility = Visibility.Collapsed;
        }

        private void ApplyLyricText(string lyricText, bool showMedia)
        {
            if (SongLyricFull == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplyLyricText(lyricText, showMedia));
                return;
            }

            if (!showMedia)
            {
                _lastLyricText = "";
                _isLyricVisible = false;
                SongLyricPreview.Text = "";
                SongLyricPreview.Visibility = Visibility.Collapsed;
                SongLyricFull.Text = "";
                SongLyricFull.Visibility = Visibility.Collapsed;
                return;
            }

            var trimmedLyric = lyricText?.Trim() ?? "";
            var hasLyric = !string.IsNullOrWhiteSpace(trimmedLyric);

            _isLyricVisible = hasLyric;
            _lastLyricText = hasLyric ? trimmedLyric : "";

            SongLyricFull.Text = "";
            SongLyricFull.Visibility = Visibility.Collapsed;
            RefreshCompactMediaText();
            ApplyMediaLayout(showMedia, _isLyricVisible);
        }

        private sealed class DbTrack
        {
            public string id { get; set; }
            public string name { get; set; }
            public List<DbArtist> artists { get; set; }
        }

        private sealed class DbArtist
        {
            public string name { get; set; }
        }

        private sealed class TrackInfo
        {
            public string Id { get; init; }
            public string Title { get; init; }
            public List<string> Artists { get; init; }
        }

        private sealed class LyricLine
        {
            public TimeSpan Time { get; init; }
            public string Text { get; init; }
        }

        private static readonly Regex LrcTimeRegex = new Regex(@"\[(\d{2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly string[] ArtistSeparators = new[] { "/", "&", ",", ";", "、", "|" };

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var collapsed = Regex.Replace(title, @"\s+", " ");
            return collapsed.Trim().ToLowerInvariant();
        }

        private static string NormalizeArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            var collapsed = Regex.Replace(artist, @"\s+", " ");
            return collapsed.Trim().ToLowerInvariant();
        }

        private static List<string> SplitArtists(string artists)
        {
            if (string.IsNullOrWhiteSpace(artists)) return new List<string>();
            var parts = artists.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        private static string ComputeMd5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            var hash = md5.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string GetCloudMusicBasePath()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var netease = System.IO.Path.Combine(local, "NetEase", "CloudMusic");
            if (Directory.Exists(netease)) return netease;
            var alt = System.IO.Path.Combine(local, "Netease", "CloudMusic");
            if (Directory.Exists(alt)) return alt;
            
            // 尝试查找 UWP版本或其他位置? 暂时只支持标准安装
            return netease; // 默认 fallback
        }

        private static string GetWebDbPath()
        {
            return System.IO.Path.Combine(GetCloudMusicBasePath(), "Library", "webdb.dat");
        }

        private static string GetLyricCachePath()
        {
            return System.IO.Path.Combine(GetCloudMusicBasePath(), "Temp");
        }

        private void EnsureTrackIndexLoaded()
        {
            try
            {
                var dbPath = GetWebDbPath();
                if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;

                var lastWrite = File.GetLastWriteTimeUtc(dbPath);
                if (_tracksByTitle != null && lastWrite == _trackIndexLastWriteUtc) return;

                var index = new Dictionary<string, List<TrackInfo>>(StringComparer.OrdinalIgnoreCase);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "select id, jsonStr from dbTrack";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var json = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(json)) continue;

                    DbTrack track = null;
                    try { track = JsonSerializer.Deserialize<DbTrack>(json, options); } catch { }
                    if (track == null || string.IsNullOrWhiteSpace(track.name)) continue;

                    var artists = track.artists?.Select(a => a?.name)
                                                .Where(n => !string.IsNullOrWhiteSpace(n))
                                                .ToList() ?? new List<string>();

                    var info = new TrackInfo
                    {
                        Id = id,
                        Title = track.name.Trim(),
                        Artists = artists
                    };

                    var key = NormalizeTitle(info.Title);
                    if (!index.TryGetValue(key, out var list))
                    {
                        list = new List<TrackInfo>();
                        index[key] = list;
                    }
                    list.Add(info);
                }

                _tracksByTitle = index;
                _trackIndexLastWriteUtc = lastWrite;
                LogDebug($"Track index loaded: {index.Count} tracks from {dbPath}");
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading track index: {ex.Message}");
            }
        }

        private string ResolveTrackId(string title, string artist)
        {
            if (_tracksByTitle == null) return null;

            var key = NormalizeTitle(title);
            if (!_tracksByTitle.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                LogDebug($"No tracks found in DB for title: {title} (Normalized: {key})");
                
                // --- 调试：模糊查找可能的匹配项 ---
                try
                {
                    var potentialMatches = _tracksByTitle.Keys
                        .Where(k => k.Contains(key) || key.Contains(k))
                        .Take(5)
                        .ToList();

                    if (potentialMatches.Count > 0)
                    {
                        LogDebug($"Did you mean one of these? {string.Join(", ", potentialMatches)}");
                    }
                    else
                    {
                         // 打印前几个看看数据库里到底是啥
                        var sample = _tracksByTitle.Keys.Take(3).ToList();
                         LogDebug($"DB Sample: {string.Join(", ", sample)}...");
                    }
                }
                catch {}
                // ------------------------------------

                return null;
            }

            if (candidates.Count == 1 || string.IsNullOrWhiteSpace(artist))
            {
                LogDebug($"Resolved track ID (single match/no artist): {candidates[0].Id} for {title}");
                return candidates[0].Id;
            }

            var artistParts = SplitArtists(artist).Select(NormalizeArtist).ToList();
            var artistSet = new HashSet<string>(artistParts);

            TrackInfo best = null;
            var bestScore = -1;
            foreach (var track in candidates)
            {
                if (track.Artists == null || track.Artists.Count == 0)
                {
                    if (best == null) best = track;
                    continue;
                }

                var score = 0;
                foreach (var a in track.Artists)
                {
                    if (artistSet.Contains(NormalizeArtist(a))) score++;
                }

                if (score == track.Artists.Count)
                {
                    return track.Id;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = track;
                }
            }

            if (best != null) LogDebug($"Resolved track ID (best match): {best.Id} for {title} - {artist} (Score: {bestScore})");
            else LogDebug($"Failed to resolve track ID for {title} - {artist}");

            return best?.Id;
        }

        private List<LyricLine> TryLoadLyricLines(string trackId)
        {
            try
            {
                var dir = GetLyricCachePath();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

                var fileName = ComputeMd5(trackId);
                var path = System.IO.Path.Combine(dir, fileName);
                if (!File.Exists(path)) return null;
                return ParseLyricFile(path);
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading lyric lines: {ex.Message}");
                return null;
            }
        }

        private static List<LyricLine> ParseLyricFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("lrc", out var lrc)) return null;
                if (!lrc.TryGetProperty("lyric", out var lyricEl)) return null;
                var lyricText = lyricEl.GetString();
                if (string.IsNullOrWhiteSpace(lyricText)) return null;
                return ParseLrc(lyricText);
            }
            catch
            {
                return null;
            }
        }

        private static List<LyricLine> ParseLrc(string lrc)
        {
            var result = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(lrc)) return result;

            var lines = lrc.Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("{", StringComparison.Ordinal)) continue;

                var matches = LrcTimeRegex.Matches(line);
                if (matches.Count == 0) continue;

                var text = line.Substring(matches[^1].Index + matches[^1].Length).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                foreach (Match m in matches)
                {
                    if (!m.Success) continue;
                    if (!int.TryParse(m.Groups[1].Value, out var min)) continue;
                    if (!int.TryParse(m.Groups[2].Value, out var sec)) continue;

                    var ms = 0;
                    var frac = m.Groups[3].Value;
                    if (!string.IsNullOrEmpty(frac))
                    {
                        if (frac.Length == 1 && int.TryParse(frac, out var v1)) ms = v1 * 100;
                        else if (frac.Length == 2 && int.TryParse(frac, out var v2)) ms = v2 * 10;
                        else if (frac.Length >= 3 && int.TryParse(frac[..3], out var v3)) ms = v3;
                    }

                    var time = new TimeSpan(0, 0, min, sec, ms);
                    result.Add(new LyricLine { Time = time, Text = text });
                }
            }

            return result.OrderBy(l => l.Time).ToList();
        }

        private static string GetLyricLineAt(TimeSpan position, List<LyricLine> lines)
        {
            if (lines == null || lines.Count == 0) return "";

            var lo = 0;
            var hi = lines.Count - 1;
            var best = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (lines[mid].Time <= position)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return best >= 0 ? lines[best].Text : "";
        }

        private async Task TryUpdateLyricLineAsync(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null) return;
            if (IsMediaSuppressed()) return;
            if (ShouldSuppressMediaForManualStandby(session))
            {
                ApplyLyricText("", false);
                return;
            }

            var now = DateTime.Now;
            if ((now - _lastLyricPoll) < _lyricPollInterval) return;
            _lastLyricPoll = now;

            if (_lyricUpdateInFlight) return;
            _lyricUpdateInFlight = true;
            try
            {
                var settings = GetSettings();
                if (!settings.ShowMediaPlayer)
                {
                    ApplyLyricText("", false);
                    return;
                }

                if (_currentSession == null || session.SourceAppUserModelId != _currentSession.SourceAppUserModelId) return;

                // 增加校验：即使 _currentSongTitle 有值，也主动对比一次，防止系统事件丢失
                var info = await session.TryGetMediaPropertiesAsync();
                if (info != null)
                {
                    if (string.IsNullOrWhiteSpace(_currentSongTitle) || 
                        !string.Equals(_currentSongTitle, info.Title, StringComparison.Ordinal))
                    {
                        // 发现标题不对应，说明发生了切歌但事件可能延迟了，强制重置状态
                        _currentSongTitle = info.Title;
                        _currentSongArtist = info.Artist;
                        _currentSubtitle = info.Subtitle;
                        _currentLyricTrackId = null; // 强制重新匹配歌词
                        _currentLyricLines = null;
                        
                        // 同步更新 UI 信息
                        _ = UpdateMediaInfo(session);
                    }
                }

                if (string.IsNullOrWhiteSpace(_currentSongTitle))
                {
                    ApplyLyricText(_currentSubtitle, settings.ShowMediaPlayer);
                    return;
                }
                
                // Optimized log: Only log if song changed or first check
                // LogDebug($"Look up: {_currentSongTitle} - {_currentSongArtist}");

                EnsureTrackIndexLoaded();
                var trackId = ResolveTrackId(_currentSongTitle, _currentSongArtist);
                
                // If local track ID not found, try API directly
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    _currentLyricLines = await GetLyricsFromApiAsync(_currentSongTitle, _currentSongArtist);
                    if (_currentLyricLines != null && _currentLyricLines.Count > 0)
                    {
                         // Keep API lyric identity stable per title+artist to avoid same-title collisions.
                         var apiKey = $"{_currentSongTitle}|{_currentSongArtist}";
                         trackId = "API_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey));
                    }
                }

                if (string.IsNullOrWhiteSpace(trackId) && (_currentLyricLines == null || _currentLyricLines.Count == 0))
                {
                    ApplyLyricText(_currentSubtitle, settings.ShowMediaPlayer);
                    return;
                }

                if (_currentLyricTrackId != trackId)
                {
                    _currentLyricTrackId = trackId;
                    _currentLyricLines = TryLoadLyricLines(trackId);
                }

                // Fallback to API if local lyrics not found
                if (_currentLyricLines == null || _currentLyricLines.Count == 0)
                {
                    _currentLyricLines = await GetLyricsFromApiAsync(_currentSongTitle, _currentSongArtist);
                }

                if (_currentLyricLines == null || _currentLyricLines.Count == 0)
                {
                    LogDebug("No lyric lines found locally or via API.");
                    ApplyLyricText(_currentSubtitle, settings.ShowMediaPlayer);
                    return;
                }

                var timeline = session.GetTimelineProperties();
                var position = timeline.Position + _lyricTimelineOffset;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                var line = GetLyricLineAt(position, _currentLyricLines);
                ApplyLyricText(line, settings.ShowMediaPlayer);
            }
            catch (Exception ex) 
            {
                LogDebug($"TryUpdateLyricLineAsync Error: {ex.Message}");
            }
            finally
            {
                _lyricUpdateInFlight = false;
            }
        }

        private async Task UpdateMediaInfo(GlobalSystemMediaTransportControlsSession session)
        {
            if (IsMediaSuppressed()) return;
            if (ShouldSuppressMediaForManualStandby(session))
            {
                EnterStandbyMode();
                return;
            }

            try
            {
                var info = await session.TryGetMediaPropertiesAsync();
                Dispatcher.Invoke(() =>
                {
                    if (info != null)
                    {
                        var settings = GetSettings();
                        bool showMedia = settings.ShowMediaPlayer;
                        bool showVisualizer = settings.ShowVisualizer;

                        if (!showMedia && !showVisualizer)
                        {
                            EnterStandbyMode();
                            return;
                        }

                        var newTitle = info.Title;
                        var newArtist = info.Artist;
                        if (!string.Equals(_currentSongTitle, newTitle, StringComparison.Ordinal) ||
                            !string.Equals(_currentSongArtist, newArtist, StringComparison.Ordinal))
                        {
                            ResetLyricState();
                            AlbumCover.Source = null; // 立即清除旧封面
                        }

                        _currentSongTitle = newTitle;
                        _currentSongArtist = newArtist;
                        _currentSubtitle = info.Subtitle;

                        ApplyMediaLayout(showMedia, showMedia && _isLyricVisible);

                        SongTitle.Text = string.IsNullOrWhiteSpace(newTitle) ? "Unknown Title" : newTitle;
                        ArtistName.Text = string.IsNullOrWhiteSpace(newArtist) ? "Unknown Artist" : newArtist;
                        SongLyricFull.Visibility = Visibility.Collapsed;
                        SongLyricFull.Text = "";
                        RefreshCompactMediaText();

                    AlbumCover.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
                    AudioSourceBadge.Visibility = (showMedia && _isExpanded) ? Visibility.Visible : Visibility.Collapsed;
                    if (showMedia)
                    {
                        UpdateAudioSourceBadgeVisual(session.SourceAppUserModelId);
                    }
                    VisualizerContainer.Visibility = (showVisualizer && !_isExpanded) ? Visibility.Visible : Visibility.Collapsed;
                    
                    if (showMedia)
                    {
                        ControlPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
                        ProgressArea.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
                        ArtistName.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;

                        if (_isExpanded)
                        {
                            ControlPanel.Opacity = 1.0;
                            ProgressArea.Opacity = 1.0;
                            ArtistName.Opacity = 1.0;
                            MainMediaArea.MaxHeight = 88;
                            UpdateAudioSourceBadgePosition(88, new Thickness(12, 4, 0, 0));
                        }
                        else
                        {
                            RestoreCompactMediaLayout();
                        }
                        
                        UpdateMediaTimeline();
                    }

                        NotificationPanel.Visibility = Visibility.Collapsed;
                        DrinkWaterPanel.Visibility = Visibility.Collapsed;
                        TodoPanel.Visibility = Visibility.Collapsed;
                        FileStationPanel.Visibility = Visibility.Collapsed;

                        DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
                        SetClickThrough(false); // 媒体播放时允许交互
                        
                        // 清除动画锁定
                        DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                        DynamicIsland.Opacity = GetActiveOpacity();

                        if (showMedia && info.Thumbnail != null) LoadThumbnail(info.Thumbnail);
                    }
                });
            }
            catch { CheckCurrentSession(); }
        }
        #endregion

        private void UpdatePlaybackStatus(GlobalSystemMediaTransportControlsSession session)
        {
            if (_isNotificationActive) return;
            if (ShouldSuppressMediaForManualStandby(session))
            {
                return;
            }
            try
            {
                var info = session.GetPlaybackInfo();
                if (info == null) return;

                Dispatcher.Invoke(() =>
                {
                    _isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var settings = GetSettings();
                    var allowVisualizer = settings.ShowVisualizer && _isPlaying && !_isExpanded;

                    if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        IconPlay.Visibility = Visibility.Collapsed;
                        IconPause.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        IconPlay.Visibility = Visibility.Visible;
                        IconPause.Visibility = Visibility.Collapsed;
                    }

                    if (VisualizerContainer != null)
                    {
                        VisualizerContainer.Visibility = allowVisualizer ? Visibility.Visible : Visibility.Collapsed;
                        if (!allowVisualizer) ResetVisualizer();
                    }
                });
            }
            catch { }
        }

        #region 4. 物理与渲染
        private void InitializePhysics()
        {
            _widthSpring = new Spring(120);
            _heightSpring = new Spring(35);
            _lastFrameTime = DateTime.Now;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (DynamicIsland == null) return;

            var now = DateTime.Now;
            var dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt > 0.05) dt = 0.05;

            var newWidth = _widthSpring.Update(dt);
            var newHeight = _heightSpring.Update(dt);

            DynamicIsland.Width = Math.Max(1, newWidth);
            DynamicIsland.Height = Math.Max(1, newHeight);

            if (DynamicIsland.Height > 0)
            {
                var radius = DynamicIsland.Height * 0.3;
                DynamicIsland.CornerRadius = new CornerRadius(0, 0, radius, radius);
            }

            if (Bar1 != null && _isPlaying && VisualizerContainer.Visibility == Visibility.Visible) 
                UpdateVisualizer();
        }

        private void ResetVisualizer()
        {
            if (Bar1 == null) return;
            Bar1.Height = 4;
            Bar2.Height = 4;
            Bar3.Height = 4;
            Bar4.Height = 4;
            Bar5.Height = 4;
        }

        private void InitializeAudioCapture()
        {
            try { _capture = new WasapiLoopbackCapture(); _capture.DataAvailable += OnAudioDataAvailable; _capture.StartRecording(); } catch { }
        }
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            float max = 0;
            for (int i = 0; i < e.BytesRecorded; i += 8)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                var normalized = Math.Abs(sample / 32768f);
                if (normalized > max) max = normalized;
            }
            _currentVolume = max;
        }
        private void UpdateVisualizer()
        {
            var time = DateTime.Now.TimeOfDay.TotalSeconds;
            var intensity = Math.Clamp(_currentVolume, 0f, 1f);
            double baseH = 4 + (intensity * 28);

            // Add per-bar rhythm + pseudo-random jitter so columns are intentionally uneven.
            double jitter1 = 0.16 * Math.Sin(time * 23.0 + 0.9);
            double jitter2 = 0.18 * Math.Cos(time * 19.0 + 2.1);
            double jitter3 = 0.22 * Math.Sin(time * 27.0 + 1.4);
            double jitter4 = 0.14 * Math.Cos(time * 17.0 + 3.7);
            double jitter5 = 0.20 * Math.Sin(time * 21.0 + 5.2);

            double h1 = Math.Max(3.5, baseH * (0.42 + 0.20 * Math.Sin(time * 9.0 + 0.6) + jitter1));
            double h2 = Math.Max(3.5, baseH * (0.58 + 0.24 * Math.Cos(time * 11.5 + 1.7) + jitter2));
            double h3 = Math.Max(3.5, baseH * (0.80 + 0.30 * Math.Sin(time * 13.0 + 2.5) + jitter3));
            double h4 = Math.Max(3.5, baseH * (0.50 + 0.26 * Math.Cos(time * 10.0 + 0.3) + jitter4));
            double h5 = Math.Max(3.5, baseH * (0.64 + 0.22 * Math.Sin(time * 12.0 + 4.1) + jitter5));

            Bar1.Height = h1;
            Bar2.Height = h2;
            Bar3.Height = h3;
            Bar4.Height = h4;
            Bar5.Height = h5;
        }
        private async void LoadThumbnail(IRandomAccessStreamReference thumbnail)
        {
            try { var s = await thumbnail.OpenReadAsync(); var b = new BitmapImage(); b.BeginInit(); b.StreamSource = s.AsStream(); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); AlbumCover.Source = b; } catch { }
        }
        private void DynamicIsland_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) 
        { 
            // 只有按住 Ctrl 键时才能拖动
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && 
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) 
            {
                ShowCenterGuide();
                try { DragMove(); }
                finally
                {
                    HideCenterGuide();
                    SnapToCenterIfNear();
                    SaveWindowPosition();
                }
                return;
            }

            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                BeginSwipeTracking(e);
            }
        }

        private void DynamicIsland_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isSwipeTracking) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                ResetSwipeTracking();
                return;
            }
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                ResetSwipeTracking();
                return;
            }
            if (_isFileStationActive || _storedFiles.Count > 0)
            {
                ResetSwipeTracking();
                return;
            }

            var pos = e.GetPosition(this);
            var dx = pos.X - _swipeStart.X;
            var dy = pos.Y - _swipeStart.Y;

            if (!_isSwiping)
            {
                if (Math.Abs(dx) < SwipeStartThreshold && Math.Abs(dy) < SwipeStartThreshold) return;
                _isSwiping = true;
                if (!DynamicIsland.IsMouseCaptured)
                {
                    DynamicIsland.CaptureMouse();
                }
            }

            // 左滑进入待机
            if (dx <= -SwipeTriggerDistance &&
                Math.Abs(dy) <= SwipeMaxVerticalDelta &&
                Math.Abs(dx) > Math.Abs(dy) * 1.5)
            {
                TriggerSwipeToStandby();
                return;
            }

            // 右滑退出待机（仅手动待机状态有效）
            if (dx >= SwipeTriggerDistance &&
                Math.Abs(dy) <= SwipeMaxVerticalDelta &&
                Math.Abs(dx) > Math.Abs(dy) * 1.5 &&
                _manualStandby)
            {
                TriggerSwipeExitStandby();
            }
        }

        private void DynamicIsland_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isSwipeTracking && !_isSwiping && !_swipeTriggered) return;
            ResetSwipeTracking();
        }

        private void DynamicIsland_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            HideIsland();
        }

        public void HideIsland()
        {
            try
            {
                Hide();
            }
            catch { }
        }

        public void ShowIsland()
        {
            try
            {
                if (!IsVisible)
                {
                    Show();
                }
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
            }
            catch { }
        }

        private void BeginSwipeTracking(System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isFileStationActive || _storedFiles.Count > 0) return;

            _isSwipeTracking = true;
            _isSwiping = false;
            _swipeTriggered = false;
            _swipeStart = e.GetPosition(this);
        }

        private void ResetSwipeTracking()
        {
            _isSwipeTracking = false;
            _isSwiping = false;
            _swipeTriggered = false;
            if (DynamicIsland.IsMouseCaptured)
            {
                DynamicIsland.ReleaseMouseCapture();
            }
        }

        private void TriggerSwipeToStandby()
        {
            if (_swipeTriggered) return;

            _swipeTriggered = true;
            if (DynamicIsland.IsMouseCaptured)
            {
                DynamicIsland.ReleaseMouseCapture();
            }

            SetManualStandbyForCurrentSession();
            DismissActivePanelsForStandby();
            EnterStandbyMode();
        }

        private void TriggerSwipeExitStandby()
        {
            if (_swipeTriggered) return;

            _swipeTriggered = true;
            if (DynamicIsland.IsMouseCaptured)
            {
                DynamicIsland.ReleaseMouseCapture();
            }

            _manualStandby = false;
            _dismissedSessionId = null;
            CheckCurrentSession();
        }

        private void SetManualStandbyForCurrentSession()
        {
            string sessionId = null;
            try
            {
                sessionId = _currentSession?.SourceAppUserModelId;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    sessionId = _mediaManager?.GetCurrentSession()?.SourceAppUserModelId;
                }
            }
            catch { }
            _manualStandby = true;
            _dismissedSessionId = sessionId;
        }

        private bool ShouldSuppressMediaForManualStandby(GlobalSystemMediaTransportControlsSession session)
        {
            if (!_manualStandby) return false;
            if (session == null) return true;
            if (string.IsNullOrWhiteSpace(_dismissedSessionId)) return true;
            return string.Equals(session.SourceAppUserModelId, _dismissedSessionId, StringComparison.OrdinalIgnoreCase);
        }

        private void DismissActivePanelsForStandby()
        {
            _isNotificationActive = false;
            _notificationTimer?.Stop();
            NotificationPanel.Visibility = Visibility.Collapsed;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;
        }
        private bool _isExpanded = false;

        private void UpdateMediaTimeline()
        {
            try
            {
                if (_currentSession == null) return;
                var timeline = _currentSession.GetTimelineProperties();
                
                Dispatcher.Invoke(() =>
                {
                    if (ProgressArea.Visibility == Visibility.Visible)
                    {
                        var pos = timeline.Position.TotalSeconds;
                        var total = timeline.EndTime.TotalSeconds;
                        if (total > 0)
                        {
                            MediaProgressBar.Value = (pos / total) * 100;
                            TxtCurrentTime.Text = $"{(int)timeline.Position.TotalMinutes}:{(int)timeline.Position.Seconds:D2}";
                            TxtTotalTime.Text = $"{(int)timeline.EndTime.TotalMinutes}:{(int)timeline.EndTime.Seconds:D2}";
                        }
                    }
                });
            }
            catch { }
        }

        private void DynamicIsland_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (AlbumCover.Visibility != Visibility.Visible) return;

            _isExpanded = true;

            var duration = TimeSpan.FromMilliseconds(340);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var expandedCoverSize = 88.0;
            var expandedCoverMargin = new Thickness(12, 4, 0, 0);
            var sizeAnim = new DoubleAnimation(expandedCoverSize, duration) { EasingFunction = ease };
            AlbumCover.BeginAnimation(WidthProperty, sizeAnim);
            AlbumCover.BeginAnimation(HeightProperty, sizeAnim);
            AlbumClip.Rect = new Rect(0, 0, expandedCoverSize, expandedCoverSize);
            AlbumCover.Margin = expandedCoverMargin;
            UpdateAudioSourceBadgePosition(expandedCoverSize, expandedCoverMargin);
            MainMediaArea.MaxHeight = expandedCoverSize;
            MainMediaArea.Margin = new Thickness(12, 4, 14, 0);
            MainMediaArea.VerticalAlignment = VerticalAlignment.Top;
            AudioSourceBadge.Visibility = Visibility.Visible;

            SongLyricPreview.Visibility = Visibility.Collapsed;
            SongTitle.Visibility = Visibility.Visible;
            SongLyricFull.Visibility = Visibility.Collapsed;

            ControlPanel.Visibility = Visibility.Visible;
            ProgressArea.Visibility = Visibility.Visible;
            ArtistName.Visibility = Visibility.Visible;
            TxtCurrentTime.Visibility = Visibility.Collapsed;
            TxtTotalTime.Visibility = Visibility.Collapsed;
            VisualizerContainer.Visibility = Visibility.Collapsed;

            var fadeIn = new DoubleAnimation(1, duration) { EasingFunction = ease };
            ControlPanel.BeginAnimation(OpacityProperty, fadeIn);
            ProgressArea.BeginAnimation(OpacityProperty, fadeIn);
            ArtistName.BeginAnimation(OpacityProperty, fadeIn);

            ApplyMediaLayout(true, _isLyricVisible);
        }

        private void DynamicIsland_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (AlbumCover.Visibility != Visibility.Visible) return;

            _isExpanded = false;
            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            var sizeAnim = new DoubleAnimation(40, duration) { EasingFunction = ease };
            AlbumCover.BeginAnimation(WidthProperty, sizeAnim);
            AlbumCover.BeginAnimation(HeightProperty, sizeAnim);
            AlbumClip.Rect = new Rect(0, 0, 40, 40);
            AlbumCover.Margin = new Thickness(10, 4, 0, 0);
            UpdateAudioSourceBadgePosition(40, AlbumCover.Margin);
            RestoreCompactMediaLayout();

            SongLyricFull.Visibility = Visibility.Collapsed;

            ApplyMediaLayout(true, _isLyricVisible);
        }

        private void ShowCenterGuide()
        {
            if (_centerGuideWindow == null)
            {
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var centerX = screenWidth / 2;

                var canvas = new Canvas { IsHitTestVisible = false };
                var line = new Line
                {
                    X1 = centerX,
                    X2 = centerX,
                    Y1 = 0,
                    Y2 = screenHeight,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 163, 122)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };
                canvas.Children.Add(line);

                _centerGuideWindow = new Window
                {
                    Width = screenWidth,
                    Height = screenHeight,
                    Left = 0,
                    Top = 0,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ShowActivated = false,
                    Content = canvas,
                    IsHitTestVisible = false
                };
                canvas.IsHitTestVisible = false;
            }

            _centerGuideWindow.Show();
        }

        private void HideCenterGuide()
        {
            _centerGuideWindow?.Hide();
        }

        private void SnapToCenterIfNear()
        {
            try
            {
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var centerX = screenWidth / 2;
                var windowCenter = this.Left + (this.Width / 2);
                if (Math.Abs(windowCenter - centerX) <= CenterSnapThreshold)
                {
                    this.Left = centerX - (this.Width / 2);
                }
            }
            catch { }
        }

        private void SaveWindowPosition()
        {
            try
            {
                var settings = AppSettings.Load();
                settings.WindowLeft = this.Left;
                settings.WindowTop = this.Top;
                settings.Save();
            }
            catch { }
        }

        #endregion

        #region File Station (Black Hole)

        private void DynamicIsland_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                if (!_isFileDragHover)
                {
                    _isFileDragHover = true;
                    _isFileStationActive = true;
                    EnterFileStationMode(dragging: true);
                }
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DynamicIsland_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                if (!_isFileDragHover)
                {
                    _isFileDragHover = true;
                    _isFileStationActive = true;
                    EnterFileStationMode(dragging: true);
                }
            }
            e.Handled = true;
        }

        private void DynamicIsland_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            if (_isFileDragHover && IsDragPointInsideDynamicIsland(e))
            {
                e.Handled = true;
                return;
            }
            _isFileDragHover = false;

            // 如果没有真正存入文件就离开了，恢复原状
            if (_storedFiles.Count == 0 && !IsMouseOver)
            {
                _isFileStationActive = false;
                CheckCurrentSession(); // 恢复媒体或待机
            }
            else if (_storedFiles.Count > 0)
            {
                // 如果有文件，恢复到紧凑的“由文件”状态
                EnterFileStationMode(dragging: false);
            }
        }

        private void DynamicIsland_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                _isFileDragHover = false;
                var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _storedFiles.AddRange(files);
                    UpdateFileStationUI();
                    
                    // 播放吸入动画序列
                    PlaySuckInSequence();
                }
            }
            // _isFileStationActive = (_storedFiles.Count > 0); // Moved to inside sequence
        }

        private bool IsDragPointInsideDynamicIsland(System.Windows.DragEventArgs e)
        {
            try
            {
                if (DynamicIsland == null) return false;
                var pos = e.GetPosition(DynamicIsland);
                return pos.X >= 0 &&
                       pos.Y >= 0 &&
                       pos.X <= DynamicIsland.ActualWidth &&
                       pos.Y <= DynamicIsland.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        private void PlaySuckInSequence()
        {
            // 1. 加速旋转并收缩 (吞噬效果)
            var consumeAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) 
            { 
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.5 } 
            };
            
            // 2. 岛屿伴随黑洞震颤
            PlayIslandGlowEffect(Colors.Purple);

            consumeAnim.Completed += (s, ev) =>
            {
                 _isFileStationActive = (_storedFiles.Count > 0);
                 EnterFileStationMode(dragging: false);
            };

            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, consumeAnim);
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, consumeAnim);
        }

        private void EnterFileStationMode(bool dragging)
        {
            HideAllMediaElements();
            NotificationPanel.Visibility = Visibility.Collapsed;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;

            // 显示中转站
            FileStationPanel.Visibility = Visibility.Visible;
            DynamicIsland.Opacity = GetActiveOpacity();
            SetClickThrough(false);

            if (dragging)
            {
                // 拖拽进入时：黑洞张开
                _widthSpring.Target = 420;
                _heightSpring.Target = 130;
                
                DropHintText.Opacity = 1;
                FileStackDisplay.Visibility = Visibility.Collapsed;
                
                // 黑洞扩张动画
                var scaleAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                
                // 漩涡旋转
                var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever };
                VortexRotation.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            }
            else
            {
                // 存储状态：紧凑显示
                ShowFileStationState();
            }
        }

        private void ShowFileStationState()
        {
            HideAllMediaElements();
            NotificationPanel.Visibility = Visibility.Collapsed;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;

            FileStationPanel.Visibility = Visibility.Visible;
            DropHintText.Opacity = 0;
            
            // 黑洞收缩
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BlackHoleScale.ScaleX = 0; 
            BlackHoleScale.ScaleY = 0;

            // 显示文件堆栈
            FileStackDisplay.Visibility = Visibility.Visible;
            UpdateFileStationUI();

            _heightSpring.Target = 72;
            DynamicIsland.Opacity = GetActiveOpacity();
            SetClickThrough(false);
        }

        private void UpdateFileStationUI()
        {
            if (FileSummaryText == null) return;

            var count = _storedFiles.Count;
            if (count <= 0)
            {
                FileSummaryText.Text = "暂无文件";
                UpdateFileStationSizeByText(FileSummaryText.Text);
                return;
            }

            var firstName = System.IO.Path.GetFileName(_storedFiles[0]);
            if (string.IsNullOrWhiteSpace(firstName))
            {
                firstName = "未知文件";
            }

            FileSummaryText.Text = count == 1
                ? firstName
                : $"{firstName} 等{count}个文件";

            UpdateFileStationSizeByText(FileSummaryText.Text);
        }

        private void UpdateFileStationSizeByText(string summary)
        {
            try
            {
                var text = summary ?? "";
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var formatted = new FormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        FileSummaryText.FontFamily,
                        FileSummaryText.FontStyle,
                        FileSummaryText.FontWeight,
                        FileSummaryText.FontStretch),
                    FileSummaryText.FontSize,
                    System.Windows.Media.Brushes.White,
                    dpi);

                // 计算：左右内边距 + 图标宽 + 图标与文本间距 + 文本宽度 + 右安全边距
                var targetWidth = 24 + 36 + 10 + formatted.WidthIncludingTrailingWhitespace + 24;
                _widthSpring.Target = Math.Clamp(targetWidth, 180, 560);
            }
            catch
            {
                _widthSpring.Target = 320;
            }
        }

        private void PlayBlackHoleSuckAnimation()
        {
            // 简单的震动反馈或闪光
            PlayIslandGlowEffect(Colors.Purple);
        }

        private void FileStack_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_storedFiles.Count > 0)
            {
                // 开始拖出操作
                var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, _storedFiles.ToArray());
                System.Windows.DragDrop.DoDragDrop(FileStackDisplay, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
                
                // 拖拽完成后清空 (假设用户拖走就是拿走了)
                // 注意：DoDragDrop 是阻塞的，直到拖拽结束
                _storedFiles.Clear();
                _isFileStationActive = false;
                
                // 恢复正常状态
                CheckCurrentSession(); 
            }
        }

        #endregion

        private async Task<List<LyricLine>> GetLyricsFromApiAsync(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            var cacheKey = $"{title}_{artist}";
            if (_fallbackLyricCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            try
            {
                // 1. First, try to search for the specific Netease ID
                string neteaseId = await SearchTrackIdAsync(title, artist);
                string url;
                
                if (!string.IsNullOrEmpty(neteaseId))
                {
                    LogDebug($"Search success. ID: {neteaseId}. Fetching lyric by ID...");
                    // Use ID to get lyric
                    url = $"https://api.paugram.com/netease/?id={neteaseId}";
                }
                else
                {
                    LogDebug("Search ID failed. Fallback to title parameter...");
                    url = $"https://api.paugram.com/netease/?title={Uri.EscapeDataString(title)}";
                    if (!string.IsNullOrWhiteSpace(artist))
                    {
                        url += $"&artist={Uri.EscapeDataString(artist)}";
                    }
                }

                LogDebug($"Requesting URL: {url}");

                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                     LogDebug($"API request failed. Status: {response.StatusCode}. Content: {json}");
                     return null;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    LogDebug("API returned empty response.");
                    return null;
                }

                // Log the first 100 chars of response to see what we got
                // LogDebug($"API Raw Response (first 100 chars): {json.Substring(0, Math.Min(json.Length, 100))}");

                var data = JsonSerializer.Deserialize<ApiLyricModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data != null && !string.IsNullOrWhiteSpace(data.Lyric))
                {
                    var lines = ParseLrc(data.Lyric);
                    if (lines != null && lines.Count > 0)
                    {
                        _fallbackLyricCache[cacheKey] = lines;
                        LogDebug($"API lyric fetched success: {lines.Count} lines");
                        return lines;
                    }
                    else
                    {
                         LogDebug("API returned lyric field but failed to parse as LRC.");
                    }
                }
                else
                {
                     LogDebug("API response parsed but 'lyric' field is empty or null.");
                }
            }
            catch (JsonException jex)
            {
                 LogDebug($"API JSON Parse Error: {jex.Message}.");
            }
            catch (Exception ex)
            {
                LogDebug($"API fetch error: {ex.Message}");
            }

            return null;
        }

        private async Task<string> SearchTrackIdAsync(string title, string artist)
        {
            try
            {
                var query = title;
                if (!string.IsNullOrWhiteSpace(artist)) query += " " + artist;
                
                LogDebug($"Searching Netease ID for: {query}");
                var searchUrl = $"http://music.163.com/api/search/get/web?csrf_token=&hlpretag=&hlposttag=&s={Uri.EscapeDataString(query)}&type=1&offset=0&total=true&limit=3";
                
                var response = await _httpClient.GetAsync(searchUrl);
                if (!response.IsSuccessStatusCode) return null;
                
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("result", out var result) && 
                    result.TryGetProperty("songs", out var songs) && 
                    songs.GetArrayLength() > 0)
                {
                    // If we have an artist to match against
                    if (!string.IsNullOrWhiteSpace(artist))
                    {
                        var targetArtist = artist.Trim().ToLowerInvariant();
                        foreach (var song in songs.EnumerateArray())
                        {
                            // Check artists array
                            if (song.TryGetProperty("artists", out var artistsArray))
                            {
                                foreach (var ar in artistsArray.EnumerateArray())
                                {
                                    if (ar.TryGetProperty("name", out var arNameElement))
                                    {
                                        var arName = arNameElement.GetString()?.Trim().ToLowerInvariant() ?? "";
                                        
                                        // Simple containment check or exact match
                                        if (!string.IsNullOrEmpty(arName) && (arName.Contains(targetArtist) || targetArtist.Contains(arName)))
                                        {
                                            if (song.TryGetProperty("id", out var bestId))
                                            {
                                                LogDebug($"Found artist match: {arNameElement.GetString()} - ID: {bestId}");
                                                return bestId.ToString();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Fallback: Return the first match's ID
                    var firstSong = songs[0];
                    if (firstSong.TryGetProperty("id", out var id))
                    {
                         LogDebug($"No artist match found. Fallback to first result ID: {id}");
                        return id.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                 LogDebug($"Search API error: {ex.Message}");
            }
            return null;
        }

        private class ApiLyricModel
        {
            public string Lyric { get; set; }
        }
    }
}
