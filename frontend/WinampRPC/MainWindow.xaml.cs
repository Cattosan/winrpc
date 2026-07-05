using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System;
using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace WinampRPC
{
    public sealed partial class MainWindow : Window
    {
        public class LyricLine : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            public TimeSpan Timestamp { get; set; }

            private string _text = "";
            public string Text
            {
                get => _text;
                set { _text = value; OnPropertyChanged(nameof(Text)); }
            }

            private Brush _color = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            public Brush Color
            {
                get => _color;
                set { _color = value; OnPropertyChanged(nameof(Color)); }
            }

            private double _scale = 0.9;
            public double Scale
            {
                get => _scale;
                set { _scale = value; OnPropertyChanged(nameof(Scale)); }
            }

            private double _opacity = 0.5;
            public double Opacity
            {
                get => _opacity;
                set { _opacity = value; OnPropertyChanged(nameof(Opacity)); }
            }
        }

        public class LrcLibResponse
        {
            public int id { get; set; }
            public string? trackName { get; set; }
            public string? artistName { get; set; }
            public string? albumName { get; set; }
            public double duration { get; set; }
            public string? syncedLyrics { get; set; }
            public string? plainLyrics { get; set; }
        }
        
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_COMMAND = 0x0111;
        private const int WINAMP_VISPLUGIN = 40192;
        private const uint WM_USER = 0x0400;
        private const int IPC_GETOUTPUTTIME = 105;

        private GoBridge.TrackUpdateCallback? _trackCb;
        private GoBridge.StatusCallback? _statusCb;
        
        private static readonly string _logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinampRPC", "logs");
        private static readonly string _logFile = System.IO.Path.Combine(_logDir, "lyrics_debug.log");
        
        static MainWindow()
        {
            if (!System.IO.Directory.Exists(_logDir))
            {
                try { System.IO.Directory.CreateDirectory(_logDir); } catch { }
            }
        }
        
        private string _clientId = "";
        private string _lastFmKey = "";
        private bool _isLightMode = false;
        private string _customWinampPath = "";
        private bool _enableVisualizer = true;
        private bool _autoStartVisualizer = true;
        
        private Rectangle[] _visBars = new Rectangle[24];
        private Rectangle[] _visPeaks = new Rectangle[24];
        private double[] _peakHeights = new double[24];
        private double[] _peakVelocities = new double[24];
        private bool _isWinampPlaying = false;
        private bool _isWinampPaused = false;
        private CancellationTokenSource _visCts = new CancellationTokenSource();
        
        private byte[] _visBuffer = new byte[24];
        private DispatcherTimer _visTimer;
        private DispatcherTimer _lyricsTimer;
        
        private string _currentFileInfoJson = "";
        private string _currentCoverUrl = "";
        private string _currentTitle = "";
        private string _currentArtist = "";
        private string _currentAlbum = "";
        private string _currentYear = "";
        
        private static readonly HttpClient _httpClient = new HttpClient();
        private ObservableCollection<LyricLine> _currentLyrics = new ObservableCollection<LyricLine>();
        private bool _isLyricsPanelOpen = false;
        private int _activeLyricIndex = -1;
        private string _lastLrcTrackKey = "";
        private bool _isCardLayoutNarrow = false;

        private Compositor? _compositor;
        private Visual? _lyricsPanelVisual;
        private float _currentLyricsScrollY = 0f;
        private bool _lyricsScrollReady = false;

        public MainWindow()
        {
            this.InitializeComponent();
            LyricsListView.ItemsSource = _currentLyrics;
            
            // Init Visualizer Rectangles
            for (int i = 0; i < 24; i++)
            {
                _visBars[i] = new Rectangle 
                { 
                    Width = 14, 
                    Height = 2, 
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Cyan),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(2, 0, 2, 0),
                    RadiusX = 2,
                    RadiusY = 2
                };
                
                _visPeaks[i] = new Rectangle 
                { 
                    Width = 14, 
                    Height = 2, 
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(2, 0, 2, 2),
                    Opacity = 0.8
                };

                VisualizerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
                Grid.SetColumn(_visBars[i], i);
                Grid.SetColumn(_visPeaks[i], i);
                
                VisualizerGrid.Children.Add(_visBars[i]);
                VisualizerGrid.Children.Add(_visPeaks[i]);
            }
            
            _visTimer = new DispatcherTimer();
            _visTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60fps
            _visTimer.Tick += VisTimer_Tick;
            _visTimer.Start();
            
            _lyricsTimer = new DispatcherTimer();
            _lyricsTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30fps
            _lyricsTimer.Tick += LyricsTimer_Tick;
            _lyricsTimer.Start();

        
            Task.Run(() => StartVisualizerClient(_visCts.Token));
            
            SystemBackdrop = new MicaBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            
            // Warm up HTTP connections on startup to pre-establish TLS handshakes
            _ = Task.Run(WarmUpHttpClients);
            
            // Phantom UI Preloader to eliminate first-click lag on menus
            PreloadUIElements();

            var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png");
            if (File.Exists(logoPath))
            {
                TitleBarIcon.Source = new BitmapImage(new Uri(logoPath));
            }

            AppWindow.Changed += AppWindow_Changed;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 850));

            // Load Config
            (_clientId, _lastFmKey, _isLightMode, _customWinampPath, _enableVisualizer, _autoStartVisualizer) = ConfigManager.Load();
            ApplyTheme();
            
            try
            {
                _trackCb = new GoBridge.TrackUpdateCallback(OnTrackUpdate);
                _statusCb = new GoBridge.StatusCallback(OnStatusUpdate);
                GoBridge.InitCallbacks(_trackCb, _statusCb);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"DLL Error: {ex.Message}";
            }

            // Ghost click to start plugin if Winamp is already running
            IntPtr winampWindow = FindWindow("Winamp v1.x", null);
            if (winampWindow != IntPtr.Zero)
            {
                SendMessage(winampWindow, WM_COMMAND, (IntPtr)WINAMP_VISPLUGIN, IntPtr.Zero);
            }
        }
        
        private async Task WarmUpHttpClients()
        {
            try
            {
                // Send lightweight HEAD requests to pre-warm the DNS and TLS pools
                var urls = new[] {
                    "https://lrclib.net",
                    "https://itunes.apple.com",
                    "https://ws.audioscrobbler.com"
                };
                
                var tasks = urls.Select(url =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Head, url);
                    req.Headers.UserAgent.ParseAdd("WinampRPC/1.0");
                    return _httpClient.SendAsync(req);
                });
                
                await Task.WhenAll(tasks);
            }
            catch { } // Ignore warmup failures silently
        }

        private void PreloadUIElements()
        {
            // Stage 1: Force .NET JIT to pre-compile all heavy event handler methods
            // This runs on a background thread — no UI needed.
            _ = Task.Run(() =>
            {
                try
                {
                    // Find and JIT-compile all the heavy handler methods ahead of time
                    string[] methodsToPreJit = {
                        "Preferences_Click", "Help_Click", "About_Click",
                        "ViewFileInfo_Click", "ThemeToggle_Click", "SyncLyricsUI",
                        "CheckAndFetchLyrics", "OnTrackUpdate", "OnStatusUpdate"
                    };

                    var type = typeof(MainWindow);
                    foreach (var name in methodsToPreJit)
                    {
                        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (method != null)
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        }
                    }
                }
                catch { }
            });

            // Stage 2 & 3: Force XAML template realization + menu flyout warmup (must be on UI thread)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    // Stage 2: Force XAML template realization for ContentDialog and controls
                    var phantomHost = new Grid { Opacity = 0, Width = 0, Height = 0 };

                    var tb1 = new TextBox { Text = "x" };
                    var tb2 = new TextBox { Text = "x" };
                    var ts1 = new ToggleSwitch();
                    var ts2 = new ToggleSwitch();
                    var panel = new StackPanel { Spacing = 10 };
                    panel.Children.Add(tb1);
                    panel.Children.Add(tb2);
                    panel.Children.Add(ts1);
                    panel.Children.Add(ts2);

                    var dialog = new ContentDialog
                    {
                        Title = "_",
                        Content = panel,
                        PrimaryButtonText = "_",
                        CloseButtonText = "_",
                        XamlRoot = this.Content.XamlRoot
                    };

                    phantomHost.Children.Add(dialog);

                    // Also pre-compile the heavy controls unique to ViewFileInfo dialog
                    var phantomHost2 = new Grid { Opacity = 0, Width = 0, Height = 0 };
                    var fileInfoGrid = new Grid { ColumnSpacing = 20 };
                    fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    
                    var sp = new StackPanel { Spacing = 10 };
                    sp.Children.Add(new TextBlock { Text = "x", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    sp.Children.Add(new TextBlock { Text = "x", TextWrapping = TextWrapping.Wrap });
                    
                    var border = new Border 
                    { 
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGray),
                        Child = sp, CornerRadius = new CornerRadius(2)
                    };
                    
                    var img = new Image { Width = 200, Height = 200, Stretch = Stretch.UniformToFill };
                    
                    fileInfoGrid.Children.Add(border);
                    fileInfoGrid.Children.Add(img);
                    
                    var dialog2 = new ContentDialog
                    {
                        Title = "_",
                        Content = fileInfoGrid,
                        CloseButtonText = "_",
                        XamlRoot = this.Content.XamlRoot
                    };
                    dialog2.Resources["ContentDialogMaxWidth"] = 1000.0;
                    phantomHost2.Children.Add(dialog2);

                    if (this.Content is Panel rootPanel)
                    {
                        rootPanel.Children.Add(phantomHost);
                        rootPanel.Children.Add(phantomHost2);
                        phantomHost.Measure(new Windows.Foundation.Size(1, 1));
                        phantomHost.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0));
                        phantomHost2.Measure(new Windows.Foundation.Size(1, 1));
                        phantomHost2.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0));
                        rootPanel.Children.Remove(phantomHost);
                        rootPanel.Children.Remove(phantomHost2);
                    }

                    // Stage 3: Flash-open each menu flyout to force WinUI3 to realize
                    // the popup infrastructure (this is the main source of first-click lag)
                    await Task.Delay(100); // Small delay to let Stage 2 finish rendering

                    var menus = new MenuBarItem[] { FileMenu, SettingsMenu, HelpMenu };
                    foreach (var menu in menus)
                    {
                        try
                        {
                            // Use UI Automation to programmatically "invoke" (open) the menu
                            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(menu);
                            if (peer != null)
                            {
                                var invokeProvider = peer.GetPattern(
                                    Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)
                                    as Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider;
                                
                                if (invokeProvider != null)
                                {
                                    invokeProvider.Expand();
                                    await Task.Delay(15);
                                    invokeProvider.Collapse();
                                    await Task.Delay(15);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange)
            {
                // Hard limit the OS window size
                if (sender.Size.Width < 1000 || sender.Size.Height < 950)
                {
                    int w = Math.Max(sender.Size.Width, 1000);
                    int h = Math.Max(sender.Size.Height, 950);
                    sender.Resize(new Windows.Graphics.SizeInt32(w, h));
                }
            }
        }

        private void ApplyTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark;
                
                var rodColor = _isLightMode ? Microsoft.UI.Colors.Indigo : Microsoft.UI.ColorHelper.FromArgb(255, 255, 153, 0); // Winamp Orange
                var peakColor = _isLightMode ? Microsoft.UI.Colors.DarkGray : Microsoft.UI.Colors.White;
                
                for (int i = 0; i < 24; i++)
                {
                    if (_visBars[i] != null)
                        _visBars[i].Fill = new SolidColorBrush(rodColor);
                    if (_visPeaks[i] != null)
                        _visPeaks[i].Fill = new SolidColorBrush(peakColor);
                }
                
                SetLyricsButtonState(_currentLyrics != null && _currentLyrics.Count > 0);
                
                var titleBar = AppWindow.TitleBar;
                if (titleBar != null)
                {
                    if (_isLightMode)
                    {
                        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                        titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(25, 0, 0, 0);
                        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                        titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(51, 0, 0, 0);
                        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                        titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DarkGray;
                    }
                    else
                    {
                        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                        titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(25, 255, 255, 255);
                        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                        titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(51, 255, 255, 255);
                        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                        titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DarkGray;
                    }
                }
            }
        }

        private void OnTrackUpdate(IntPtr pTitle, IntPtr pArtist, IntPtr pAlbum, IntPtr pYear, IntPtr pQuality, IntPtr pCoverUrl, IntPtr pFileInfoJson)
        {
            string title = Marshal.PtrToStringUTF8(pTitle) ?? "";
            string artist = Marshal.PtrToStringUTF8(pArtist) ?? "";
            string album = Marshal.PtrToStringUTF8(pAlbum) ?? "";
            string year = Marshal.PtrToStringUTF8(pYear) ?? "";
            string quality = Marshal.PtrToStringUTF8(pQuality) ?? "";
            string coverUrl = Marshal.PtrToStringUTF8(pCoverUrl) ?? "";
            string fileInfoJson = Marshal.PtrToStringUTF8(pFileInfoJson) ?? "";

            DispatcherQueue.TryEnqueue(() =>
            {
                _currentTitle = title;
                _currentArtist = artist;
                _currentAlbum = album;
                _currentYear = year;
                _currentCoverUrl = coverUrl;
                _currentFileInfoJson = fileInfoJson;
                
                bool isPaused = false;
                if (!string.IsNullOrEmpty(_currentFileInfoJson))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(_currentFileInfoJson);
                        if (doc.RootElement.TryGetProperty("isPaused", out JsonElement valPaused))
                        {
                            isPaused = valPaused.GetBoolean();
                        }
                    } catch {}
                }
                
                _isWinampPaused = isPaused;

                if (title == "No Winamp Detected" || title == "Stopped")
                {
                    _isWinampPlaying = false;
                }
                else
                {
                    _isWinampPlaying = true;
                }

                if (title == "No Winamp Detected" || title == "Stopped")
                {
                    NowPlayingLabel.Text = title == "Stopped" ? "Not Playing" : "Now Playing";
                    TitleText.Text = title == "Stopped" ? "Stopped" : "Waiting for Winamp...";
                    DiscordTitleText.Text = title == "Stopped" ? "Stopped" : "Idle";
                    DiscordProgressGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                else if (isPaused)
                {
                    NowPlayingLabel.Text = "Now Paused";
                    TitleText.Text = string.IsNullOrEmpty(title) ? "Waiting for Winamp..." : title;
                    DiscordTitleText.Text = "None";
                    DiscordProgressGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                else
                {
                    NowPlayingLabel.Text = "Now Playing";
                    TitleText.Text = string.IsNullOrEmpty(title) ? "Waiting for Winamp..." : title;
                    DiscordTitleText.Text = string.IsNullOrEmpty(title) ? "Idle" : title;
                    DiscordProgressGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }

                if (title == "No Winamp Detected")
                {
                    SyncToggle.IsEnabled = false;
                    StartWinampButton.IsEnabled = true;
                    StartWinampButton.Content = "Start Winamp";
                }
                else
                {
                    SyncToggle.IsEnabled = true;
                    StartWinampButton.IsEnabled = false;
                    StartWinampButton.Content = "Winamp Started";
                }
                
                if (string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(album))
                {
                    ArtistAlbumText.Text = "";
                    DiscordArtistText.Text = "";
                    DiscordAlbumText.Text = "";
                }
                else
                {
                    ArtistAlbumText.Text = $"{artist}\n{album}\n{year}";
                    DiscordArtistText.Text = artist;
                    DiscordAlbumText.Text = album;
                }
                
                QualityText.Text = string.IsNullOrEmpty(quality) ? "Unknown Quality" : quality;

                if (!string.IsNullOrEmpty(coverUrl) && coverUrl != "music_icon")
                {
                    try
                    {
                        if (coverUrl.StartsWith("http"))
                        {
                            var uri = new Uri(coverUrl);
                            AlbumArtImage.Source = new BitmapImage(uri);
                            DiscordCoverImage.Source = new BitmapImage(uri);
                        }
                        else if (File.Exists(coverUrl))
                        {
                            // Load from MemoryStream to bypass WinUI URI caching
                            var bytes = File.ReadAllBytes(coverUrl);
                            using (var ms = new MemoryStream(bytes))
                            {
                                var randomAccessStream = ms.AsRandomAccessStream();
                                
                                var bmp1 = new BitmapImage();
                                bmp1.SetSource(randomAccessStream);
                                
                                var bmp2 = new BitmapImage();
                                // Reset stream position for second read
                                randomAccessStream.Seek(0);
                                bmp2.SetSource(randomAccessStream);
                                
                                AlbumArtImage.Source = bmp1;
                                DiscordCoverImage.Source = bmp2;
                            }
                        }
                        else
                        {
                            AlbumArtImage.Source = null;
                            DiscordCoverImage.Source = null;
                        }
                    }
                    catch
                    {
                        AlbumArtImage.Source = null;
                        DiscordCoverImage.Source = null;
                    }
                }
                else
                {
                    AlbumArtImage.Source = null;
                    DiscordCoverImage.Source = null;
                }
                
                if (!string.IsNullOrEmpty(_currentFileInfoJson))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(_currentFileInfoJson);
                        if (doc.RootElement.TryGetProperty("length", out JsonElement valLen))
                        {
                            string? lengthStr = valLen.GetString()?.Replace(" seconds", "").Trim();
                            if (int.TryParse(lengthStr, out int totalSecs))
                            {
                                DiscordStartText.Text = FormatTime(totalSecs / 2);
                                DiscordLengthText.Text = FormatTime(totalSecs);
                            }
                            else
                            {
                                DiscordStartText.Text = "0:00";
                                DiscordLengthText.Text = valLen.GetString();
                            }
                        }
                        else
                        {
                            DiscordStartText.Text = "0:00";
                            DiscordLengthText.Text = "0:00";
                        }
                    }
                    catch { }
                }

                CheckAndFetchLyrics();
            });
        }

        private void OnStatusUpdate(IntPtr pMsg)
        {
            string msg = Marshal.PtrToStringUTF8(pMsg) ?? "";
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusText.Text = msg;
            });
        }

        private void SyncToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SyncToggle.Content.ToString() == "Start Sync")
            {
                if (string.IsNullOrWhiteSpace(_clientId))
                {
                    StatusText.Text = "Please set Client ID in Preferences";
                    return;
                }
                
                SyncToggle.Content = "Stop Sync";
                UpdateRpcButton.IsEnabled = true;
                GoBridge.StartPresence(_clientId, _lastFmKey);
            }
            else
            {
                GoBridge.StopPresence();
                SyncToggle.Content = "Start Sync";
                UpdateRpcButton.IsEnabled = false;
                StatusText.Text = "Stopped";
            }
        }
        
        private void UpdateRpc_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_clientId)) return;
            
            GoBridge.StopPresence();
            GoBridge.StartPresence(_clientId, _lastFmKey);
            StatusText.Text = "RPC Updated";
        }
        
        private async void ViewFileInfo_Click(object sender, RoutedEventArgs e)
        {
            string trackNum = "N/A", discNum = "N/A", publisher = "N/A", channels = "N/A";
            string bitDepth = "N/A", sampleRate = "N/A", bitrate = "N/A", format = "N/A";
            string length = "N/A", fileSize = "N/A";
            
            if (!string.IsNullOrEmpty(_currentFileInfoJson))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(_currentFileInfoJson);
                    JsonElement root = doc.RootElement;
                    
                    if (root.TryGetProperty("trackNum", out JsonElement valTrack) && !string.IsNullOrEmpty(valTrack.GetString())) trackNum = valTrack.GetString() ?? "N/A";
                    if (root.TryGetProperty("discNum", out JsonElement valDisc) && !string.IsNullOrEmpty(valDisc.GetString())) discNum = valDisc.GetString() ?? "N/A";
                    if (root.TryGetProperty("publisher", out JsonElement valPub) && !string.IsNullOrEmpty(valPub.GetString())) publisher = valPub.GetString() ?? "N/A";
                    if (root.TryGetProperty("channels", out JsonElement valChan) && !string.IsNullOrEmpty(valChan.GetString())) channels = valChan.GetString() ?? "N/A";
                    if (root.TryGetProperty("bitDepth", out JsonElement valDepth) && !string.IsNullOrEmpty(valDepth.GetString())) bitDepth = valDepth.GetString() ?? "N/A";
                    if (root.TryGetProperty("sampleRate", out JsonElement valSample) && !string.IsNullOrEmpty(valSample.GetString())) sampleRate = valSample.GetString() ?? "N/A";
                    if (root.TryGetProperty("bitrate", out JsonElement valBitrate) && !string.IsNullOrEmpty(valBitrate.GetString())) bitrate = valBitrate.GetString() ?? "N/A";
                    if (root.TryGetProperty("format", out JsonElement valFormat) && !string.IsNullOrEmpty(valFormat.GetString())) format = valFormat.GetString() ?? "N/A";
                    if (root.TryGetProperty("length", out JsonElement valLen) && !string.IsNullOrEmpty(valLen.GetString())) length = valLen.GetString() ?? "N/A";
                    if (root.TryGetProperty("fileSize", out JsonElement valSize) && !string.IsNullOrEmpty(valSize.GetString())) fileSize = valSize.GetString() ?? "N/A";
                }
                catch { }
            }
            
            var grid = new Grid { ColumnSpacing = 20, MinWidth = 800 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            // Left Panel (Metadata GroupBox)
            var metadataPanel = new StackPanel { Spacing = 10, Margin = new Thickness(12, 18, 12, 12) };
            metadataPanel.Children.Add(new TextBlock { Text = $"Track: {trackNum}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            metadataPanel.Children.Add(new TextBlock { Text = $"Disc: {discNum}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            metadataPanel.Children.Add(new TextBlock { Text = $"Title: {_currentTitle}", TextWrapping = TextWrapping.Wrap });
            metadataPanel.Children.Add(new TextBlock { Text = $"Artist: {_currentArtist}", TextWrapping = TextWrapping.Wrap });
            metadataPanel.Children.Add(new TextBlock { Text = $"Album: {_currentAlbum}", TextWrapping = TextWrapping.Wrap });
            metadataPanel.Children.Add(new TextBlock { Text = $"Year: {_currentYear}" });
            metadataPanel.Children.Add(new TextBlock { Text = $"Publisher: {publisher}", TextWrapping = TextWrapping.Wrap });

            var leftPanelBorder = new Border 
            { 
                BorderThickness = new Thickness(1), 
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGray),
                Child = metadataPanel,
                Margin = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(2)
            };

            var leftHeaderText = new TextBlock 
            { 
                Text = "Metadata", 
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            
            var leftHeaderBorder = new Border
            {
                Child = leftHeaderText,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            
            if (!_isLightMode) leftHeaderBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
            else leftHeaderBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

            var leftPanel = new Grid();
            leftPanel.Children.Add(leftPanelBorder);
            leftPanel.Children.Add(leftHeaderBorder);
            
            // Center Panel (Album Cover or Placeholder)
            Image coverImg = new Image { Width = 200, Height = 200, Stretch = Stretch.UniformToFill };
            bool hasCover = false;
            
            if (!string.IsNullOrEmpty(_currentCoverUrl) && _currentCoverUrl != "music_icon")
            {
                try 
                { 
                    coverImg.Source = new BitmapImage(new Uri(_currentCoverUrl)); 
                    hasCover = true;
                } 
                catch { }
            }
            
            FrameworkElement centerElement;
            if (hasCover)
            {
                centerElement = coverImg;
            }
            else
            {
                centerElement = new Border
                {
                    Width = 200,
                    Height = 200,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGray),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock
                    {
                        Text = "Album Cover",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
                    }
                };
            }
            
            // Right Panel (Format Info GroupBox)
            var formatInfoPanel = new StackPanel { Spacing = 5, Margin = new Thickness(12, 18, 12, 12) };
            if (format != "N/A") formatInfoPanel.Children.Add(new TextBlock { Text = $"Format: {format}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            formatInfoPanel.Children.Add(new TextBlock { Text = $"Length: {length}" });
            formatInfoPanel.Children.Add(new TextBlock { Text = $"Channels: {channels}" });
            formatInfoPanel.Children.Add(new TextBlock { Text = $"Bits per sample: {bitDepth}" });
            formatInfoPanel.Children.Add(new TextBlock { Text = $"Sample Rate: {sampleRate}" });
            
            string displayFileSize = fileSize;
            if (long.TryParse(fileSize, out long bytes))
            {
                string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
                int counter = 0;
                decimal number = (decimal)bytes;
                while (Math.Round(number / 1024) >= 1)
                {
                    number /= 1024;
                    counter++;
                }
                displayFileSize = string.Format("{0:n2} {1}", number, suffixes[counter]);
            }
            formatInfoPanel.Children.Add(new TextBlock { Text = $"File Size: {displayFileSize}" });
            
            formatInfoPanel.Children.Add(new TextBlock { Text = $"Average bitrate: {bitrate}" });

            var rightPanelBorder = new Border 
            { 
                BorderThickness = new Thickness(1), 
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGray),
                Child = formatInfoPanel,
                Margin = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(2)
            };

            var headerText = new TextBlock 
            { 
                Text = "Format Info", 
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            
            var headerBorder = new Border
            {
                Child = headerText,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            
            if (!_isLightMode) headerBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
            else headerBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

            var rightPanel = new Grid();
            rightPanel.Children.Add(rightPanelBorder);
            rightPanel.Children.Add(headerBorder);
            
            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(centerElement, 1);
            Grid.SetColumn(rightPanel, 2);
            
            grid.Children.Add(leftPanel);
            grid.Children.Add(centerElement);
            grid.Children.Add(rightPanel);

            ContentDialog dialog = new ContentDialog
            {
                Title = "File Information",
                Content = grid,
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark
            };
            
            // WinUI 3 strictly caps ContentDialog width. We must override this specific resource to make it wider!
            dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
            
            await dialog.ShowAsync();
        }
        
        private async void StartWinamp_Click(object sender, RoutedEventArgs e)
        {
            ApplyWinampIniSettings();

            if (!string.IsNullOrEmpty(_customWinampPath) && File.Exists(_customWinampPath))
            {
                try { Process.Start(_customWinampPath); return; } catch { }
            }
            
            try
            {
                Process.Start("winamp.exe");
                return;
            }
            catch { }
            
            string defaultPath = @"C:\Program Files (x86)\Winamp\winamp.exe";
            if (File.Exists(defaultPath))
            {
                try { Process.Start(defaultPath); return; } catch { }
            }
            
            // Fallback: Show dialog
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "Winamp Not Found",
                    Content = "Could not find Winamp. Would you like to locate it manually, or download it?",
                    PrimaryButtonText = "Browse...",
                    SecondaryButtonText = "Download",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark
                };
                
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    picker.FileTypeFilter.Add(".exe");
                    picker.FileTypeFilter.Add(".lnk");
                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        _customWinampPath = file.Path;
                        ConfigManager.Save(_clientId, _lastFmKey, _isLightMode, _customWinampPath, _enableVisualizer, _autoStartVisualizer);
                        try { Process.Start(_customWinampPath); } catch { }
                    }
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    Process.Start(new ProcessStartInfo { FileName = "https://winamp.com/player", UseShellExecute = true });
                }
            }
        }

        private void ApplyWinampIniSettings()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string winampIni = System.IO.Path.Combine(appData, "Winamp", "winamp.ini");
                
                if (File.Exists(winampIni))
                {
                    string[] lines = File.ReadAllLines(winampIni);
                    bool foundName = false;
                    bool foundAutoExec = false;
                    
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("visplugin_name=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = "visplugin_name=vis_winamprpc.dll";
                            foundName = true;
                        }
                        else if (lines[i].StartsWith("visplugin_autoexec=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = _autoStartVisualizer ? "visplugin_autoexec=1" : "visplugin_autoexec=0";
                            foundAutoExec = true;
                        }
                    }
                    
                    var newLines = new System.Collections.Generic.List<string>(lines);
                    
                    // If keys don't exist, append them under [Winamp] section
                    if (!foundName || !foundAutoExec)
                    {
                        int sectionIndex = -1;
                        for (int i = 0; i < newLines.Count; i++)
                        {
                            if (newLines[i].Trim().Equals("[Winamp]", StringComparison.OrdinalIgnoreCase))
                            {
                                sectionIndex = i;
                                break;
                            }
                        }
                        
                        if (sectionIndex != -1)
                        {
                            if (!foundName) newLines.Insert(sectionIndex + 1, "visplugin_name=vis_winamprpc.dll");
                            if (!foundAutoExec) newLines.Insert(sectionIndex + 1, _autoStartVisualizer ? "visplugin_autoexec=1" : "visplugin_autoexec=0");
                        }
                    }
                    
                    File.WriteAllLines(winampIni, newLines);
                }
            }
            catch { }
        }

        
        
        private void LyricsTimer_Tick(object? sender, object e)
        {
            SyncLyricsUI();
        }

        private void VisTimer_Tick(object? sender, object e)
        {
            
            if (!_enableVisualizer) return;
            
            for (int i = 0; i < 24; i++)
            {
                double targetHeight = 2;
                if (_isWinampPlaying && !_isWinampPaused)
                {
                    targetHeight = Math.Max(2, (_visBuffer[i] / 255.0) * 200.0);
                    _visBars[i].Height = _visBars[i].Height + (targetHeight - _visBars[i].Height) * 0.4;
                }
                else if (!_isWinampPlaying)
                {
                    // Smooth transition for the main bar to fall when completely stopped
                    _visBars[i].Height = _visBars[i].Height + (targetHeight - _visBars[i].Height) * 0.4;
                }

                // Peak Physics (Gravity)
                if (_isWinampPlaying && !_isWinampPaused && targetHeight >= _peakHeights[i])
                {
                    _peakHeights[i] = targetHeight;
                    _peakVelocities[i] = 0.0;
                }
                else
                {
                    _peakVelocities[i] += 0.8; // Gravity acceleration
                    _peakHeights[i] -= _peakVelocities[i];
                    if (_peakHeights[i] < _visBars[i].Height)
                    {
                        _peakHeights[i] = _visBars[i].Height;
                        _peakVelocities[i] = 0.0;
                    }
                }

                _visPeaks[i].Margin = new Thickness(2, 0, 2, _peakHeights[i] + 2); // Position peak above bar
            }
        }

        private void SyncLyricsUI()
        {
            if (!_isLyricsPanelOpen || _currentLyrics.Count == 0 || !_isWinampPlaying) return;

            IntPtr hwnd = FindWindow("Winamp v1.x", null);
            if (hwnd == IntPtr.Zero) return;

            int posMs = (int)SendMessage(hwnd, WM_USER, IntPtr.Zero, (IntPtr)IPC_GETOUTPUTTIME);
            if (posMs < 0) return;

            TimeSpan currentPos = TimeSpan.FromMilliseconds(posMs);
            int newActiveIndex = -1;

            for (int i = 0; i < _currentLyrics.Count; i++)
            {
                if (currentPos >= _currentLyrics[i].Timestamp)
                    newActiveIndex = i;
                else
                    break;
            }

            if (newActiveIndex == _activeLyricIndex || newActiveIndex == -1) return;

            int oldIndex = _activeLyricIndex;
            _activeLyricIndex = newActiveIndex;

            // Only animate the two lines that actually changed
            if (oldIndex >= 0 && oldIndex < _currentLyrics.Count)
                AnimateLyricLine(oldIndex, false);

            AnimateLyricLine(newActiveIndex, true);

            // Scroll active line to center smoothly without delay since layout sizes are now static
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ScrollLyricToCenter(newActiveIndex));
        }

        private void AnimateLyricLine(int index, bool activate)
        {
            try
            {
                var container = LyricsListView.ContainerFromIndex(index) as ListViewItem;
                if (container == null) return;

                var tb = FindChildTextBlock(container);
                if (tb == null) return;

                double targetOpacity = activate ? 1.0 : 0.4;
                var targetColor = activate
                    ? (_isLightMode ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White)
                    : Microsoft.UI.Colors.Gray;

                // Storyboard for opacity with CubicEase
                var sb = new Storyboard();

                var opacityAnim = new DoubleAnimation
                {
                    To = targetOpacity,
                    Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    EnableDependentAnimation = false
                };
                Storyboard.SetTarget(opacityAnim, tb);
                Storyboard.SetTargetProperty(opacityAnim, "Opacity");
                sb.Children.Add(opacityAnim);

                // ColorAnimation on the brush's Color property
                if (tb.Foreground is SolidColorBrush brush)
                {
                    var animBrush = new SolidColorBrush(brush.Color);
                    tb.Foreground = animBrush;

                    var colorAnim = new ColorAnimation
                    {
                        To = targetColor,
                        Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                        EnableDependentAnimation = true
                    };
                    Storyboard.SetTarget(colorAnim, animBrush);
                    Storyboard.SetTargetProperty(colorAnim, "Color");
                    sb.Children.Add(colorAnim);
                }

                double targetScale = activate ? 1.0 : 0.9;
                _currentLyrics[index].Scale = targetScale;

                if (tb.RenderTransform is ScaleTransform st)
                {
                    var scaleXAnim = new DoubleAnimation { To = targetScale, Duration = new Duration(TimeSpan.FromMilliseconds(350)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, EnableDependentAnimation = false };
                    var scaleYAnim = new DoubleAnimation { To = targetScale, Duration = new Duration(TimeSpan.FromMilliseconds(350)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, EnableDependentAnimation = false };
                    Storyboard.SetTarget(scaleXAnim, st);
                    Storyboard.SetTarget(scaleYAnim, st);
                    Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
                    Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
                    sb.Children.Add(scaleXAnim);
                    sb.Children.Add(scaleYAnim);
                }

                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AnimateLyricLine error: {ex.Message}");
            }
        }

        private TextBlock FindChildTextBlock(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb) return tb;
                var result = FindChildTextBlock(child);
                if (result != null) return result;
            }
            return null!;
        }

        private void ScrollLyricToCenter(int index)
        {
            try
            {
                var sv = GetListViewScrollViewer(LyricsListView);
                if (sv == null) return;

                var container = LyricsListView.ContainerFromIndex(index) as ListViewItem;
                if (container == null) return;

                var panel = LyricsListView.ItemsPanelRoot;
                if (panel == null) return;

                var transform = container.TransformToVisual(panel);
                var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

                double targetY = position.Y - (sv.ViewportHeight / 2.0) + (container.ActualHeight / 2.0);
                targetY = Math.Max(0, Math.Min(targetY, sv.ScrollableHeight));

                float targetYF = (float)targetY;
                float delta = targetYF - _currentLyricsScrollY;

                if (Math.Abs(delta) < 0.5f) return; // No scroll needed

                // Initialize composition on first use
                if (!_lyricsScrollReady)
                {
                    var panelRoot = LyricsListView.ItemsPanelRoot;
                    if (panelRoot == null) return;
                    
                    // CRITICAL: Enable Translation facade BEFORE animating Translation.Y
                    ElementCompositionPreview.SetIsTranslationEnabled(panelRoot, true);
                    
                    var visual = ElementCompositionPreview.GetElementVisual(panelRoot);
                    if (visual == null) return;
                    
                    _lyricsPanelVisual = visual;
                    _compositor = visual.Compositor;
                    _lyricsScrollReady = true;
                }

                if (_compositor == null || _lyricsPanelVisual == null) return;

                // 1. Jump the ScrollViewer instantly to the new offset
                sv.ChangeView(null, targetY, null, true);  // true = disable built-in animation

                // 2. Apply a Composition translation animation on the panel visual
                var slideAnim = _compositor.CreateScalarKeyFrameAnimation();
                slideAnim.InsertKeyFrame(0.0f, delta);
                slideAnim.InsertKeyFrame(1.0f, 0.0f, _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f)));
                slideAnim.Duration = TimeSpan.FromMilliseconds(350);

                _lyricsPanelVisual.StartAnimation("Translation.Y", slideAnim);
                _currentLyricsScrollY = targetYF;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ScrollLyricToCenter error: {ex.Message}");
            }
        }
        
        private void LyricsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // A container is being (re)assigned to an item — this is the moment ListView
            // virtualization/recycling would otherwise leave a stale highlight behind.
            // Force the container's visuals to match the correct active/inactive state for
            // whatever item it now represents, so exactly one line is ever highlighted.
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is not ListViewItem container) return;

            void ApplyState()
            {
                var tb = FindChildTextBlock(container);
                if (tb == null) return;

                bool isActive = args.ItemIndex == _activeLyricIndex;
                var color = isActive
                    ? (_isLightMode ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White)
                    : Microsoft.UI.Colors.Gray;

                tb.Foreground = new SolidColorBrush(color);
                tb.Opacity = isActive ? 1.0 : 0.4;

                double scale = isActive ? 1.0 : 0.9;
                if (tb.RenderTransform is ScaleTransform st)
                {
                    st.ScaleX = scale;
                    st.ScaleY = scale;
                }
            }

            // The item template content may not be realized on phase 0; retry next phase if so.
            if (FindChildTextBlock(container) == null)
                args.RegisterUpdateCallback((s, a) => ApplyState());
            else
                ApplyState();
        }

        private ScrollViewer GetListViewScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = GetListViewScrollViewer(child);
                if (result != null) return result;
            }
            return null!;
        }

        private void CardsContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCardsHeight();
        }

        private void UpdateCardsHeight()
        {
            if (CardsContainerGrid == null || CardsAreaGrid == null) return;
            double maxHeight = _isCardLayoutNarrow ? 375 : 275;
            // Subtract 256px (220px visualizer + 36px bottom margin) from available container height
            double availableHeight = Math.Max(0, CardsContainerGrid.ActualHeight - 256);
            CardsAreaGrid.Height = Math.Min(availableHeight, maxHeight);
        }

        private void CardsArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double cardWidth = e.NewSize.Width / 2 - 12;
            bool shouldBeNarrow = cardWidth < 280;
            
            if (shouldBeNarrow != _isCardLayoutNarrow)
            {
                _isCardLayoutNarrow = shouldBeNarrow;
                UpdateCardLayout(shouldBeNarrow);
                UpdateCardsHeight();
            }
        }

        private void UpdateCardLayout(bool narrow)
        {
            if (narrow)
            {
                // Switch Winamp card to vertical layout
                WinampContentGrid.ColumnDefinitions.Clear();
                WinampContentGrid.RowDefinitions.Clear();
                WinampContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                WinampContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                
                Grid.SetColumn(WinampAlbumBorder, 0);
                Grid.SetRow(WinampAlbumBorder, 0);
                WinampAlbumBorder.Margin = new Thickness(0, 0, 0, 12);
                
                Grid.SetColumn(WinampMetadataPanel, 0);
                Grid.SetRow(WinampMetadataPanel, 1);
                
                // Switch Discord card to vertical layout
                DiscordContentGrid.ColumnDefinitions.Clear();
                DiscordContentGrid.RowDefinitions.Clear();
                DiscordContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                DiscordContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                
                Grid.SetColumn(DiscordAlbumBorder, 0);
                Grid.SetRow(DiscordAlbumBorder, 0);
                DiscordAlbumBorder.Margin = new Thickness(0, 0, 0, 12);
                
                Grid.SetColumn(DiscordMetadataPanel, 0);
                Grid.SetRow(DiscordMetadataPanel, 1);
            }
            else
            {
                // Switch Winamp card to horizontal layout
                WinampContentGrid.RowDefinitions.Clear();
                WinampContentGrid.ColumnDefinitions.Clear();
                WinampContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                WinampContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
                Grid.SetRow(WinampAlbumBorder, 0);
                Grid.SetColumn(WinampAlbumBorder, 0);
                WinampAlbumBorder.Margin = new Thickness(0, 0, 16, 0);
                
                Grid.SetRow(WinampMetadataPanel, 0);
                Grid.SetColumn(WinampMetadataPanel, 1);
                
                // Switch Discord card to horizontal layout
                DiscordContentGrid.RowDefinitions.Clear();
                DiscordContentGrid.ColumnDefinitions.Clear();
                DiscordContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                DiscordContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
                Grid.SetRow(DiscordAlbumBorder, 0);
                Grid.SetColumn(DiscordAlbumBorder, 0);
                DiscordAlbumBorder.Margin = new Thickness(0, 0, 16, 0);
                
                Grid.SetRow(DiscordMetadataPanel, 0);
                Grid.SetColumn(DiscordMetadataPanel, 1);
            }
        }

        private async void CheckAndFetchLyrics()
        {
            string trackKey = _currentTitle + _currentArtist;
            if (string.IsNullOrEmpty(_currentTitle) || _currentTitle == "No Winamp Detected" || _currentTitle == "Stopped" || trackKey == _lastLrcTrackKey)
            {
                if (string.IsNullOrEmpty(_currentTitle) || _currentTitle == "No Winamp Detected" || _currentTitle == "Stopped")
                {
                    SetLyricsButtonState(false);
                }
                return;
            }

            _lastLrcTrackKey = trackKey;
            _currentLyrics.Clear();
            _activeLyricIndex = -1;
            SetLyricsButtonState(false);

            int durationSecs = 0;
            if (!string.IsNullOrEmpty(_currentFileInfoJson))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(_currentFileInfoJson);
                    if (doc.RootElement.TryGetProperty("length", out JsonElement valLen))
                    {
                        string? lengthStr = valLen.GetString()?.Replace(" seconds", "").Trim();
                        int.TryParse(lengthStr, out durationSecs);
                    }
                }
                catch { }
            }

            try
            {
                var queryParams = new List<string>
                {
                    $"track_name={Uri.EscapeDataString(_currentTitle)}"
                };
                if (!string.IsNullOrEmpty(_currentArtist)) queryParams.Add($"artist_name={Uri.EscapeDataString(_currentArtist)}");
                if (!string.IsNullOrEmpty(_currentAlbum)) queryParams.Add($"album_name={Uri.EscapeDataString(_currentAlbum)}");

                string query = string.Join("&", queryParams);
                string url = $"https://lrclib.net/api/search?{query}";
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("WinampRPC/1.0");
                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    
                    // Offload heavy JSON parsing to a background thread to prevent UI stutter
                    var results = await Task.Run(() => JsonSerializer.Deserialize<List<LrcLibResponse>>(json));
                    LrcLibResponse? bestMatch = null;

                    if (results != null && results.Count > 0)
                    {
                        // Filter for entries that actually have synced lyrics
                        var syncedResults = await Task.Run(() => results.Where(r => !string.IsNullOrEmpty(r.syncedLyrics)).ToList());
                        
                        if (syncedResults.Count > 0)
                        {
                            if (durationSecs > 0)
                            {
                                // Find the closest match by duration, allowing up to a 3-second offset
                                bestMatch = await Task.Run(() => syncedResults
                                    .Where(r => Math.Abs(r.duration - durationSecs) <= 3.0)
                                    .OrderBy(r => Math.Abs(r.duration - durationSecs))
                                    .FirstOrDefault());
                            }
                            
                            if (bestMatch == null)
                            {
                                bestMatch = syncedResults.FirstOrDefault();
                            }
                        }
                    }

                    if (bestMatch != null && !string.IsNullOrEmpty(bestMatch.syncedLyrics))
                    {
                        _ = Task.Run(() => File.AppendAllText(_logFile, $"[Lyrics] Found synced match (ID: {bestMatch.id}, Dur: {bestMatch.duration}s, {bestMatch.syncedLyrics.Length} chars)" + "\n"));
                        ParseLrc(bestMatch.syncedLyrics);
                        if (_currentLyrics.Count > 0)
                        {
                            SetLyricsButtonState(true);
                            var sv = GetListViewScrollViewer(LyricsListView);
                            sv?.ChangeView(null, 0, null, true);
                        }
                        else
                        {
                            SetLyricsButtonState(false);
                        }
                    }
                    else
                    {
                        _ = Task.Run(() => File.AppendAllText(_logFile, $"[Lyrics] No synced lyrics found in {results?.Count ?? 0} search results." + "\n"));
                        SetLyricsButtonState(false);
                    }
                }
                else
                {
                    string body = await response.Content.ReadAsStringAsync();
                    _ = Task.Run(() => File.AppendAllText(_logFile, $"[Lyrics] HTTP error {response.StatusCode}: {body}" + "\n"));
                    SetLyricsButtonState(false);
                }
            }
            catch (Exception ex)
            {
                _ = Task.Run(() => File.AppendAllText(_logFile, $"[Lyrics] Exception: {ex.Message}" + "\n"));
                SetLyricsButtonState(false);
            }
        }

        private void ParseLrc(string lrcText)
        {
            _currentLyrics.Clear();
            var lines = lrcText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");
            var inactiveColor = new SolidColorBrush(Microsoft.UI.Colors.Gray);

            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    int m = int.Parse(match.Groups[1].Value);
                    int s = int.Parse(match.Groups[2].Value);
                    
                    string msStr = match.Groups[3].Value;
                    int ms = 0;
                    if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                    else if (msStr.Length == 3) ms = int.Parse(msStr);
                    
                    string text = match.Groups[4].Value.Trim();
                    if (string.IsNullOrEmpty(text)) text = "♪"; // musical note for instrumental breaks

                    _currentLyrics.Add(new LyricLine
                    {
                        Timestamp = new TimeSpan(0, 0, m, s, ms),
                        Text = text,
                        Color = inactiveColor
                    });
                }
            }
        }

        private void SetLyricsButtonState(bool hasLyrics)
        {
            LyricsToggleButton.IsEnabled = hasLyrics;
            UpdateLyricsButtonIcon(hasLyrics);
            if (!hasLyrics && _isLyricsPanelOpen)
            {
                LyricsToggleButton.IsChecked = false;
                ToggleLyricsPanel(false);
            }
        }

        private void UpdateLyricsButtonIcon(bool hasLyrics)
        {
            string assetName;
            if (!hasLyrics)
            {
                assetName = "lnf_btn.svg";
            }
            else if (_isLyricsPanelOpen)
            {
                assetName = _isLightMode ? "lf_btn_lightthm_slctd.svg" : "lf_btn_darkthm_slctd.svg";
            }
            else
            {
                assetName = _isLightMode ? "lf_btn_lightthm.svg" : "lf_btn_darkthm.svg";
            }
            LyricsButtonIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri($"ms-appx:///Assets/{assetName}"));
        }

        private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleLyricsPanel(LyricsToggleButton.IsChecked == true);
            UpdateLyricsButtonIcon(_currentLyrics != null && _currentLyrics.Count > 0);
        }

        private void ToggleLyricsPanel(bool open)
        {
            _isLyricsPanelOpen = open;

            Storyboard storyboard = new Storyboard();
            DoubleAnimation animation = new DoubleAnimation
            {
                To = open ? 374 : 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EnableDependentAnimation = true,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, LyricsPanelWrapper);
            Storyboard.SetTargetProperty(animation, "Width");
            storyboard.Children.Add(animation);
            storyboard.Begin();
            
            if (open)
            {
                // Force sync immediately
                _lyricsScrollReady = false;
                _activeLyricIndex = -1; 
                SyncLyricsUI();
            }
        }

        private async Task StartVisualizerClient(CancellationToken token)
        {
            bool hasAttemptedGhostClick = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", "WinampRPC_Vis", PipeDirection.In))
                    {
                        await client.ConnectAsync(1000, token);
                        
                        hasAttemptedGhostClick = false; // Reset the flag once successfully connected
                        
                        byte[] buffer = new byte[24];
                        
                        while (client.IsConnected && !token.IsCancellationRequested)
                        {
                            int bytesRead = await client.ReadAsync(buffer, 0, 24, token);
                            if (bytesRead == 24)
                            {
                                Array.Copy(buffer, _visBuffer, 24);
                            }
                        }
                    }
                }
                catch
                {
                    // Pipe not found or Winamp closed, wait before retry

                    if (_autoStartVisualizer && _isWinampPlaying && !hasAttemptedGhostClick)
                    {
                        IntPtr hwnd = FindWindow("Winamp v1.x", null);
                        if (hwnd != IntPtr.Zero)
                        {
                            hasAttemptedGhostClick = true;
                            SendMessage(hwnd, WM_COMMAND, (IntPtr)WINAMP_VISPLUGIN, IntPtr.Zero);
                        }
                    }

                    await Task.Delay(1000, token);
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }
        
        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isLightMode = !_isLightMode;
            ApplyTheme();
            ConfigManager.Save(_clientId, _lastFmKey, _isLightMode, _customWinampPath, _enableVisualizer, _autoStartVisualizer);
        }
        
        private async void Preferences_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Spacing = 10 };
            
            var discordBox = new TextBox { Header = "Discord Client ID", Text = _clientId == "Discord App ID here" ? "" : _clientId, PlaceholderText = "Use default ID", Width = 300 };
            var lastfmBox = new TextBox { Header = "Last.fm API Key (Optional)", Text = _lastFmKey == "Last.fm API key here" ? "" : _lastFmKey, PlaceholderText = "Use default key", Width = 300 };
            var visualizerToggle = new ToggleSwitch { Header = "Enable Visualizer", IsOn = _enableVisualizer };
            var autoStartToggle = new ToggleSwitch { Header = "Auto Start Visualizer Plugin on Winamp", IsOn = _autoStartVisualizer };
            
            panel.Children.Add(discordBox);
            panel.Children.Add(lastfmBox);
            panel.Children.Add(visualizerToggle);
            panel.Children.Add(autoStartToggle);
            
            ContentDialog dialog = new ContentDialog
            {
                Title = "Preferences",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark
            };
            
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _clientId = string.IsNullOrWhiteSpace(discordBox.Text) ? "Discord App ID here" : discordBox.Text.Trim();
                _lastFmKey = string.IsNullOrWhiteSpace(lastfmBox.Text) ? "Last.fm API key here" : lastfmBox.Text.Trim();
                _enableVisualizer = visualizerToggle.IsOn;
                _autoStartVisualizer = autoStartToggle.IsOn;
                ConfigManager.Save(_clientId, _lastFmKey, _isLightMode, _customWinampPath, _enableVisualizer, _autoStartVisualizer);
            }
        }
        
        private async void Help_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "Help",
                Content = "\nTo use WinampRPC:\n\n1. Go to Settings > Preferences.\n2. Enter your Discord App Client ID.\n3. Optionally enter a Last.fm API Key for better album art fetching.\n4. Click 'Start Sync' on the main window.\n\nEnsure Winamp is running and then play a song.\n\nLyrics Powered By LRCLIB.NET\nhttp://lrclib.net",
                CloseButtonText = "Got it",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark
            };
            await dialog.ShowAsync();
        }
        
        private async void About_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "About WinampRPC",
                Content = "\nWinampRPC v1.0\n\nA modern, accurate Discord Rich Presence integration for Winamp. Built with C# WinUI 3 and Go. \n\nMade by Cattosan.",
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = _isLightMode ? ElementTheme.Light : ElementTheme.Dark
            };
            await dialog.ShowAsync();
        }
        private string FormatTime(int totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }
}
