using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.AudioViewer;
using QuickLook.Plugin.AudioViewer.Audio;
using QuickLook.Plugin.AudioViewer.Controls;

namespace QuickLook.Plugin.AudioViewer.Harness
{
    /// <summary>
    /// Development harness: opens a real audio file with the plugin's control in a real WPF
    /// window and reports what the preview actually costs — open, scan, first paint, idle CPU
    /// and the per-tick cost of moving the playhead. Also generates test WAVs so the numbers
    /// are reproducible without a media library.
    /// </summary>
    /// <remarks>
    /// Usage:
    ///   WaveformHarness --make-test out.wav [seconds]
    ///   WaveformHarness file.wav [dark|light]
    /// </remarks>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return MainCore(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("harness failed: " + ex.GetType().FullName + ": " + ex.Message);
                return 1;
            }
        }

        private static int MainCore(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage: WaveformHarness --make-test out.wav [seconds]");
                Console.WriteLine("       WaveformHarness <audio file> [dark|light]");
                return 2;
            }

            if (args[0] == "--make-test") return MakeTest(args);

            var path = Path.GetFullPath(args[0]);
            var theme = "dark";
            _play = false;
            _click = false;
            _pause = false;
            _autoPlayProbe = false;
            _screenshot = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "light" || args[i] == "dark") theme = args[i];
                else if (args[i] == "--play") _play = true;
                else if (args[i] == "--click") _click = true;
                else if (args[i] == "--pause") _pause = true;
                else if (args[i] == "--autoplay") _autoPlayProbe = true;
                else if (args[i] == "--screenshot" && i + 1 < args.Length) _screenshot = args[++i];
            }
            if (!File.Exists(path))
            {
                Console.WriteLine("no such file: " + path);
                return 2;
            }

            AppDomain.MonitoringIsEnabled = true;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int exit = 0;
            app.Startup += (s, e) =>
            {
                try
                {
                    Run(path, theme);   // starts the measurement chain; Shutdown ends app.Run
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    exit = 1;
                    Application.Current.Shutdown();
                }
            };
            app.Run();
            return exit;
        }

        /// <summary>Report what the preview costs, then exit.</summary>
        private static void Run(string path, string theme)
        {
            var info = new FileInfo(path);
            Console.WriteLine("file           : " + path);
            Console.WriteLine("size           : " + (info.Length / 1024.0 / 1024.0).ToString("0.00") + " MB");
            Console.WriteLine("settings file  : "
                + Path.Combine(SettingHelper.LocalDataPath, "QuickLook.Plugin.AudioViewer.config")
                + "  (portable=" + SettingHelper.IsPortableVersion() + ")");

            long allocMark = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            var sw = Stopwatch.StartNew();
            using (var reader = AudioReaderFactory.Create(path))
            {
                sw.Stop();
                Console.WriteLine("format         : " + reader.WaveFormat.SampleRate + " Hz, "
                    + reader.WaveFormat.Channels + " ch, " + reader.TotalTime.ToString(@"mm\:ss\.fff"));
                Console.WriteLine("open reader    : " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");

                sw.Restart();
                var scan = WaveformData.Compute(reader, 8192);
                sw.Stop();
                Console.WriteLine("scan (8192 col): " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms  ("
                    + scan.ChannelCount + " ch x " + scan.Columns + " cols)");
            }
            long scanAlloc = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocMark;
            Console.WriteLine("scan alloc     : " + (scanAlloc / 1024.0 / 1024.0).ToString("0.0") + " MB");

            // ---- the real control in a real window ----
            var context = new ContextObject { Theme = theme == "light" ? Themes.Light : Themes.Dark };
            var total = Stopwatch.StartNew();
            var control = new WaveformPreviewControl(path, context);
            double loadedMs = -1;
            control.Loaded += (s, e) => loadedMs = total.Elapsed.TotalMilliseconds;

            var window = new Window
            {
                Width = 900,
                Height = 260,
                Title = "Waveform harness",
                Background = Brushes.Black,
                Content = control
            };
            var view = (WaveformView)control.FindName("Waveform");
            Console.WriteLine("volume at open : "
                + ((System.Windows.Controls.Slider)control.FindName("VolumeSlider")).Value.ToString("0.00")
                + "  (read from the settings file at load)");

            var process = Process.GetCurrentProcess();
            var cpuAtShow = process.TotalProcessorTime;
            window.Show();

            double firstDataMs = -1;
            var poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            poll.Tick += (s, e) =>
            {
                if (view.Data == null || firstDataMs >= 0) return;
                poll.Stop();
                firstDataMs = total.Elapsed.TotalMilliseconds;
                var cpu = process.TotalProcessorTime - cpuAtShow;

                Console.WriteLine("control loaded : " + loadedMs.ToString("0.0") + " ms");
                Console.WriteLine("first waveform : " + firstDataMs.ToString("0.0") + " ms");
                Console.WriteLine("cpu to first   : " + cpu.TotalMilliseconds.ToString("0") + " ms");

                if (_screenshot != null)
                {
                    // Wait for the final scan result rather than the first progressive
                    // snapshot, or the picture shows only the part scanned so far.
                    var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    settle.Tick += (s1, e1) =>
                    {
                        settle.Stop();
                        SaveScreenshot(control, _screenshot);
                        ClosePreview(window);
                        Application.Current.Shutdown();
                    };
                    settle.Start();
                    return;
                }

                MeasureIdleThenTicks(window, control, view, path, context);
            };
            poll.Start();
        }

        /// <summary>Idle CPU with nothing happening, then the per-tick cost of the playhead.</summary>
        private static void MeasureIdleThenTicks(Window window, FrameworkElement control, WaveformView view,
            string path, ContextObject context)
        {
            var process = Process.GetCurrentProcess();

            // Let the post-load work (the first raster, JIT) drain before sampling, so the
            // idle figure is steady state rather than a one-off cost.
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            settle.Tick += (s0, e0) =>
            {
                settle.Stop();
                var cpuIdleStart = process.TotalProcessorTime;
                var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                idle.Tick += (s, e) =>
                {
                    idle.Stop();
                    double idleMs = (process.TotalProcessorTime - cpuIdleStart).TotalMilliseconds;
                    Console.WriteLine("idle CPU, 2 s  : " + idleMs.ToString("0.0") + " ms ("
                        + (idleMs / 2000.0 * 100).ToString("0.00") + " % of one core)");

                    // A full repaint of the waveform: what the old plugin did once per tick.
                    var sw = Stopwatch.StartNew();
                    view.InvalidateVisual();
                    Pump(view.Dispatcher);
                    sw.Stop();
                    Console.WriteLine("full repaint   : " + sw.Elapsed.TotalMilliseconds.ToString("0.00") + " ms");

                    // What this plugin does once per tick: move the playhead (a render transform).
                    var shift = (TranslateTransform)control.FindName("PlayheadShift");
                    double width = Math.Max(1, view.ActualWidth);
                    for (int i = 0; i < 30; i++)
                    {
                        shift.X = i / 30.0 * width;
                        Pump(view.Dispatcher);
                    }

                    const int ticks = 300;
                    sw.Restart();
                    for (int i = 0; i < ticks; i++)
                    {
                        shift.X = i % 100 / 100.0 * width;
                        Pump(view.Dispatcher);
                    }
                    sw.Stop();
                    Console.WriteLine("playhead tick  : " + (sw.Elapsed.TotalMilliseconds / ticks).ToString("0.00")
                        + " ms/tick (" + ticks + " ticks, transform + repaint)");

                    Console.WriteLine("total alloc    : "
                        + (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize / 1024.0 / 1024.0).ToString("0.0")
                        + " MB");

                    // Move the volume on this preview, so the next one can be checked against it.
                    var volume = (System.Windows.Controls.Slider)control.FindName("VolumeSlider");
                    volume.Value = 0.35;

                    if (_autoPlayProbe)
                    {
                        var auto = (System.Windows.Controls.CheckBox)control.FindName("AutoPlayCheck");
                        auto.IsChecked = true;
                        Console.WriteLine("autoplay set   : " + auto.IsChecked
                            + "  (the next preview should start playing on its own)");
                    }

                    MeasureWarmReopen(window, path, context);
                };
                idle.Start();
            };
            settle.Start();
        }

        /// <summary>
        /// Opening the next file with WPF already warm — what pressing Space on another track
        /// costs in a running QuickLook, without this process's one-time XAML/WPF startup.
        /// </summary>
        private static void MeasureWarmReopen(Window first, string path, ContextObject context)
        {
            var sw = Stopwatch.StartNew();
            var control = new WaveformPreviewControl(path, context);
            double loadedMs = -1;
            control.Loaded += (s, e) => loadedMs = sw.Elapsed.TotalMilliseconds;

            var window = new Window
            {
                Width = 900,
                Height = 260,
                Background = Brushes.Black,
                Content = control
            };
            var view = (WaveformView)control.FindName("Waveform");
            var carried = (System.Windows.Controls.Slider)control.FindName("VolumeSlider");
            Console.WriteLine("volume in next : " + carried.Value.ToString("0.00")
                + "  (0.35 set on the previous preview)");
            window.Show();

            var poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            poll.Tick += (s, e) =>
            {
                if (view.Data == null) return;
                poll.Stop();
                Console.WriteLine("warm reopen    : shown " + loadedMs.ToString("0.0")
                    + " ms, first waveform " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");

                if (_autoPlayProbe)
                {
                    // Nothing has been clicked in this window: if the clock moves, auto-play did it.
                    var clock = (System.Windows.Controls.TextBlock)control.FindName("TimeText");
                    var auto = (System.Windows.Controls.CheckBox)control.FindName("AutoPlayCheck");
                    Console.WriteLine("autoplay state : checkbox=" + auto.IsChecked
                        + "  icon=" + IconState(control) + "  clock at open=" + clock.Text);

                    var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                    wait.Tick += (s2, e2) =>
                    {
                        wait.Stop();
                        Console.WriteLine("autoplay later : icon=" + IconState(control)
                            + "  clock=" + clock.Text + "  (nothing was clicked)");
                        ClosePreview(window);
                        ClosePreview(first);
                        Application.Current.Shutdown();
                    };
                    wait.Start();
                    return;
                }

                if (_pause) PauseProbe(window, control, first);
                else if (_click) ClickProbe(window, control, first);
                else if (_play) PlaybackProbe(window, control, first);
                else
                {
                    ClosePreview(window);
                    ClosePreview(first);
                    Application.Current.Shutdown();
                }
            };
            poll.Start();
        }

        /// <summary>
        /// Play, pause from the bottom button, wait, resume from the same button, and report the
        /// clock and the button's label at each step. A pause that only silences the output but
        /// keeps the clock running would show up as a clock that advances while paused.
        /// </summary>
        private static void PauseProbe(Window window, FrameworkElement control, Window first)
        {
            var clock = (System.Windows.Controls.TextBlock)control.FindName("TimeText");
            var button = (System.Windows.Controls.Button)control.FindName("PlayPauseButton");

            var area = (System.Windows.Controls.Grid)control.FindName("Area");
            var view = (WaveformView)control.FindName("Waveform");

            // Start playing the same way a user does: a click on the waveform.
            MoveUnderCursor(window, view, 0.2);
            foreach (var routed in new[] { UIElement.MouseLeftButtonDownEvent, UIElement.MouseLeftButtonUpEvent })
            {
                area.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = routed });
            }

            var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
            var log = new System.Collections.Generic.List<string>();
            int phase = 0, frame = 0;

            var step = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            step.Tick += (s, e) =>
            {
                switch (phase)
                {
                    case 0:                        // let it play for about a second
                        if (++frame < 4) return;
                        log.Add("播放中   : " + clock.Text + "   图标=" + IconState(control));
                        button.RaiseEvent(click);
                        log.Add("按下暂停 : " + clock.Text + "   图标=" + IconState(control));
                        phase = 1; frame = 0;
                        break;

                    case 1:                        // hold it paused for about a second
                        if (++frame < 4) return;
                        log.Add("暂停中   : " + clock.Text + "   图标=" + IconState(control));
                        button.RaiseEvent(click);
                        log.Add("再次播放 : " + clock.Text + "   图标=" + IconState(control));
                        phase = 2; frame = 0;
                        break;

                    case 2:
                        if (++frame < 4) return;
                        step.Stop();
                        log.Add("恢复后   : " + clock.Text + "   图标=" + IconState(control));
                        foreach (var line in log) Console.WriteLine("pause probe    : " + line);
                        ClosePreview(window);
                        ClosePreview(first);
                        Application.Current.Shutdown();
                        break;
                }
            };
            step.Start();
        }

        private static bool _play;
        private static bool _click;
        private static bool _pause;
        private static bool _autoPlayProbe;
        private static string _screenshot;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Point32 { public int X, Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point32 p);

        /// <summary>
        /// The cursor cannot be moved reliably from here, so move the window under it until
        /// the pointer sits at <paramref name="fraction"/> of the waveform's width.
        /// </summary>
        private static void MoveUnderCursor(Window window, FrameworkElement view, double fraction)
        {
            Point32 cursor;
            if (!GetCursorPos(out cursor)) return;
            var wanted = view.PointToScreen(new Point(view.ActualWidth * fraction, view.ActualHeight / 2));
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Left += (cursor.X - wanted.X) / dpi.DpiScaleX;
            window.Top += (cursor.Y - wanted.Y) / dpi.DpiScaleY;
            window.UpdateLayout();
        }

        /// <summary>
        /// Click the waveform where a user would — a press, a short hold, a release — and sample
        /// the playhead X every frame, so a jump back and forth shows up as numbers instead of
        /// as a hunch. The cursor cannot be moved reliably from here, so the window is moved
        /// under it until the pointer sits at 40 % of the waveform.
        /// </summary>
        private static void ClickProbe(Window window, FrameworkElement control, Window first)
        {
            var area = (System.Windows.Controls.Grid)control.FindName("Area");
            var shift = (TranslateTransform)control.FindName("PlayheadShift");
            var view = (WaveformView)control.FindName("Waveform");

            MoveUnderCursor(window, view, 0.4);

            Console.WriteLine("pointer at     : x=" + System.Windows.Input.Mouse.GetPosition(view).X.ToString("0.0")
                + " of " + view.ActualWidth.ToString("0") + " px");

            // The volume slider sits on top of the seek surface; hit-test its centre to prove a
            // press there lands on the slider and not on the waveform.
            var volume = (System.Windows.Controls.Slider)control.FindName("VolumeSlider");
            var centre = volume.TranslatePoint(
                new Point(volume.ActualWidth / 2, volume.ActualHeight / 2), control);
            var hit = control.InputHitTest(centre) as DependencyObject;
            var chain = new System.Collections.Generic.List<string>();
            for (var node = hit; node != null && chain.Count < 4; node = VisualTreeHelper.GetParent(node))
                chain.Add(node.GetType().Name);
            Console.WriteLine("hit at volume  : " + string.Join(" < ", chain));
            Console.WriteLine("volume click   : IsMoveToPointEnabled=" + volume.IsMoveToPointEnabled
                + " (a click on the track should jump the thumb there)");

            var samples = new System.Collections.Generic.List<double>();
            var held = new System.Collections.Generic.List<double>();
            var after = new System.Collections.Generic.List<double>();
            var clock = (System.Windows.Controls.TextBlock)control.FindName("TimeText");

            var press = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent
            };
            var release = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent
            };

            int phase = 0, frame = 0;
            double pressedAt = 0, worstBack = 0;
            string clockAtRelease = "";

            var sample = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            sample.Tick += (s, e) =>
            {
                switch (phase)
                {
                    case 0:                     // press, then watch while the button is held
                        area.RaiseEvent(press);
                        pressedAt = shift.X;
                        phase = 1;
                        break;

                    case 1:
                        held.Add(shift.X);
                        if (++frame >= 20) { phase = 2; frame = 0; }
                        break;

                    case 2:                     // release: playback should start here
                        clockAtRelease = clock.Text;
                        area.RaiseEvent(release);
                        phase = 3;
                        break;

                    case 3:
                        after.Add(shift.X);
                        if (++frame < 25) break;

                        sample.Stop();
                        samples.AddRange(held);
                        samples.AddRange(after);
                        for (int i = 1; i < samples.Count; i++)
                        {
                            double step = samples[i] - samples[i - 1];
                            if (step < 0) worstBack = Math.Max(worstBack, -step);
                        }

                        Console.WriteLine("pressed at     : x=" + pressedAt.ToString("0.0")
                            + " of " + view.ActualWidth.ToString("0") + " px");
                        Console.WriteLine("held (20)      : " + string.Join(" ",
                            held.ConvertAll(v => v.ToString("0.0"))));
                        Console.WriteLine("released (25)  : " + string.Join(" ",
                            after.ConvertAll(v => v.ToString("0.0"))));
                        Console.WriteLine("clock at release: " + clockAtRelease + " -> " + clock.Text);
                        Console.WriteLine("largest step back: " + worstBack.ToString("0.00") + " px"
                            + "   (the release seeks to the REAL cursor, which this harness"
                            + " cannot move; only the held phase is a fair sample)");
                        ClosePreview(window);
                        ClosePreview(first);
                        Application.Current.Shutdown();
                        break;
                }
            };
            sample.Start();
        }

        /// <summary>Render the preview to a PNG, over the dark background QuickLook uses.</summary>
        private static void SaveScreenshot(FrameworkElement control, string outPath)
        {
            control.UpdateLayout();
            int width = Math.Max(1, (int)Math.Round(control.ActualWidth));
            int height = Math.Max(1, (int)Math.Round(control.ActualHeight));

            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            preview.Render(control);

            // The control is transparent by design, so composite it onto a window colour.
            var composed = new DrawingVisual();
            using (var dc = composed.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null,
                    new Rect(0, 0, width, height));
                dc.DrawImage(preview, new Rect(0, 0, width, height));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(composed);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(outPath)) encoder.Save(file);
            Console.WriteLine("screenshot     : " + Path.GetFullPath(outPath));
        }

        /// <summary>
        /// Start playback the way a user does — by clicking near the start of the waveform —
        /// then report whether the clock advances and what playing costs.
        /// </summary>
        private static void PlaybackProbe(Window window, FrameworkElement control, Window first)
        {
            var clock = (System.Windows.Controls.TextBlock)control.FindName("TimeText");
            var status = (System.Windows.Controls.TextBlock)control.FindName("StatusText");
            var volume = (System.Windows.Controls.Slider)control.FindName("VolumeSlider");

            // Move the volume before anything plays: the level has to survive being set on a
            // control that has no player yet, and reach it when playback starts.
            volume.Value = 0.35;
            Console.WriteLine("volume set     : " + volume.Value.ToString("0.00"));

            string before = clock.Text;
            var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;

            var area = (System.Windows.Controls.Grid)control.FindName("Area");
            MoveUnderCursor(window, (FrameworkElement)control.FindName("Waveform"), 0.05);
            foreach (var routed in new[] { UIElement.MouseLeftButtonDownEvent, UIElement.MouseLeftButtonUpEvent })
            {
                area.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = routed });
            }

            var marks = new System.Collections.Generic.List<string>();
            var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            probe.Tick += (s, e) =>
            {
                marks.Add(clock.Text.Split(' ')[0]);
                if (marks.Count < 8) return;

                probe.Stop();
                double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                Console.WriteLine("playback clock : " + string.Join(" ", marks));
                if (status.Visibility == Visibility.Visible)
                    Console.WriteLine("playback status: " + status.Text);
                Console.WriteLine("playback CPU   : " + cpuMs.ToString("0.0") + " ms over "
                    + (marks.Count * 0.4).ToString("0.0") + " s ("
                    + (cpuMs / (marks.Count * 400.0) * 100).ToString("0.00") + " % of one core)");
                ClosePreview(window);
                ClosePreview(first);
                Application.Current.Shutdown();
            };
            probe.Start();
        }

        /// <summary>Which glyph the play/pause button is showing — the direct "is it playing" tell.</summary>
        private static string IconState(FrameworkElement control)
        {
            var icon = (System.Windows.Shapes.Path)control.FindName("PlayIcon");
            if (icon?.Data == null) return "(none)";
            string data = icon.Data.ToString();
            return data.Contains("3,0") ? "暂停" : "播放";
        }

        /// <summary>Flush the dispatcher down to idle so layout and render work has run.</summary>
        private static void Pump(Dispatcher dispatcher) =>
            dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

        /// <summary>
        /// Close a preview the way the host does — ask it to unload first, so teardown runs and a
        /// volume change still inside the debounce window is written out. Then report what the
        /// plugin stored, which proves the write path was reached even where this process cannot
        /// create files outside the workspace.
        /// </summary>
        private static void ClosePreview(Window window)
        {
            (window.Content as WaveformPreviewControl)?.Unload();
            window.Close();
            Console.WriteLine("volume stored  : "
                + SettingHelper.Get("Volume", -1.0, "QuickLook.Plugin.AudioViewer").ToString("0.00"));
        }

        // ---- test signal generation ----

        private static int MakeTest(string[] args)
        {
            string path = args.Length > 1 ? args[1] : "test.wav";
            double seconds = args.Length > 2
                ? double.Parse(args[2], CultureInfo.InvariantCulture)
                : 180;

            var format = new WaveFormat(44100, 16, 2);
            var random = new Random(1);
            const int framesPerWrite = 1024;
            var buffer = new byte[format.BlockAlign * framesPerWrite];

            using (var writer = new WaveFileWriter(path, format))
            {
                long frames = (long)(format.SampleRate * seconds);
                int inBuffer = 0;

                for (long f = 0; f < frames; f++)
                {
                    double t = (double)f / format.SampleRate;
                    // Sweeping tone with a slow tremolo, plus a percussive tick every 0.5 s so
                    // the waveform has both sustained shape and isolated transients.
                    double tone = Math.Sin(2 * Math.PI * (180 + 900 * Math.Sin(t * 0.6)) * t)
                                  * 0.55 * (0.25 + 0.75 * Math.Abs(Math.Sin(t * 1.7)));
                    double tick = f % (format.SampleRate / 2) == 0 ? 0.85 : 0.0;
                    short left = Pcm16(tone + tick * (random.NextDouble() - 0.5));
                    short right = Pcm16(tone * 0.8 + tick * (random.NextDouble() - 0.5));

                    int i = inBuffer * format.BlockAlign;
                    buffer[i] = (byte)left;
                    buffer[i + 1] = (byte)(left >> 8);
                    buffer[i + 2] = (byte)right;
                    buffer[i + 3] = (byte)(right >> 8);

                    if (++inBuffer == framesPerWrite)
                    {
                        writer.Write(buffer, 0, buffer.Length);
                        inBuffer = 0;
                    }
                }

                if (inBuffer > 0) writer.Write(buffer, 0, inBuffer * format.BlockAlign);
            }

            Console.WriteLine("wrote " + path + " (" + (new FileInfo(path).Length / 1024 / 1024) + " MB)");
            return 0;
        }

        private static short Pcm16(double sample)
        {
            double scaled = Math.Round(sample * short.MaxValue);
            if (scaled > short.MaxValue) return short.MaxValue;
            if (scaled < short.MinValue) return short.MinValue;
            return (short)scaled;
        }
    }
}
