using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.AudioViewer.Audio;

namespace QuickLook.Plugin.AudioViewer
{
    /// <summary>
    /// The whole preview: a waveform of the entire file, fitted to the window, plus playback.
    /// Click or drag anywhere on the waveform to seek; clicking also starts playback.
    /// </summary>
    /// <remarks>
    /// Deliberately small. There is no spectrogram, no zoom, no scrolling and no settings,
    /// so nothing here can recompute on a timer: the only per-frame work while playing is
    /// moving the playhead (a render transform) and rewriting the time text once a second.
    /// </remarks>
    public partial class WaveformPreviewControl : UserControl
    {
        /// <summary>
        /// Columns the waveform scan is stored at. Fixed and independent of the window width,
        /// so resizing re-reduces a few thousand columns instead of re-decoding the file. The
        /// view renders at no more than ~0.8 columns per device pixel, so this covers a window
        /// up to ~5000 px wide without the scan becoming the limit.
        /// </summary>
        private const int ScanColumns = 4096;

        /// <summary>Playhead update interval while playing; playback pauses stop the timer.</summary>
        private const int TickMilliseconds = 33;

        // The plugin's settings file, shared with the 1.x plugin: its AutoPlay is read and its
        // other keys are left untouched.
        private const string SettingsDomain = "QuickLook.Plugin.AudioViewer";
        private const string AutoPlayKey = "AutoPlay";
        private const string VolumeKey = "Volume";
        private const double DefaultVolume = 0.8;

        private readonly string _path;
        private readonly ContextObject _context;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // The reader is opened up front (it gives the duration and the waveform scan), but the
        // WASAPI output is not: a device stream that is initialised and never played still
        // costs CPU on its own thread, and opening a preview should cost nothing until the user
        // asks for sound. The first click therefore creates the player and hands it the stream.
        private WaveStream _stream;
        private AudioPlayer _player;
        private bool _playerFailed;
        private bool _openFailed;

        /// <summary>
        /// Output volume. Kept in a static so it survives the preview that set it, and written
        /// to the plugin's settings file so it survives QuickLook itself.
        /// </summary>
        private static float _volume = LoadVolume();

        // Guards OnVolumeChanged while the control seeds the slider from _volume: without it a
        // markup-set Value would write 0.8 into the remembered level on every new preview.
        private bool _settingVolume;
        private DispatcherTimer _volumeSaveTimer;
        private bool _volumeDirty;

        /// <summary>
        /// Play on open. A static so every preview in the session agrees, and persisted, so it
        /// survives QuickLook — otherwise a fresh install has no way to turn it on at all.
        /// </summary>
        private static bool _autoPlay = LoadAutoPlay();

        // Same trap as _settingVolume: seeding the checkbox must not look like a user change.
        private bool _settingAutoPlay;

        private DispatcherTimer _timer;
        private double _durationSeconds;
        private double _lastFraction;
        private int _lastSecond = -1;
        private int _idleTicks;
        private bool _scrubbing;
        private bool _unloaded;

        // What the play/pause button currently says, so syncing it costs a comparison rather
        // than a Content write on every tick.
        private bool _buttonShowsPause;

        // The playhead runs off this stopwatch, anchored at the last seek/play, so it advances
        // smoothly instead of following the reader's position (which runs ahead by the output
        // buffer and steps when the device changes state). See Tick().
        private readonly Stopwatch _clock = new Stopwatch();
        private double _anchorSeconds;

        public WaveformPreviewControl(string path, ContextObject context)
        {
            InitializeComponent();
            _path = path;
            _context = context;
            ApplyTheme();
            // The host can resolve or change its theme after we are built; detached in Unload().
            if (_context != null) _context.PropertyChanged += OnContextPropertyChanged;
            _settingVolume = true;
            VolumeSlider.Value = _volume;   // pick up the level this session is already at
            _settingVolume = false;
            _settingAutoPlay = true;
            AutoPlayCheck.IsChecked = _autoPlay;
            _settingAutoPlay = false;
            SyncPlayButton();               // "播放", disabled until the file is open
            Waveform.SizeChanged += (s, e) => UpdatePlayhead(_lastFraction);
            SizeChanged += (s, e) => UpdateFormatVisibility();
            Loaded += OnLoaded;
        }

        // ------------------------------------------------------------------ theme

        private void ApplyTheme()
        {
            bool dark = ResolveIsDark();
            Waveform.WaveColor = dark ? Color.FromRgb(0xC6, 0xC6, 0xC6) : Color.FromRgb(0x48, 0x48, 0x48);
            Playhead.Fill = Frozen(dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x14, 0x14, 0x14));
            var subdued = Frozen(dark ? Color.FromRgb(0x96, 0x96, 0x96) : Color.FromRgb(0x64, 0x64, 0x64));
            TimeText.Foreground = subdued;
            FormatText.Foreground = Frozen(dark ? Color.FromRgb(0xE2, 0xE2, 0xE2) : Color.FromRgb(0x2A, 0x2A, 0x2A));   // brighter than the clock: it sits next to a run of digits and must read as a label
            StatusText.Foreground = subdued;
            // A CheckBox inherits its label colour from the control tree, which is black by
            // default — invisible on the dark background.
            AutoPlayCheck.Foreground = subdued;
        }

        private bool ResolveIsDark()
        {
            switch (_context?.Theme)
            {
                case Themes.Dark: return true;
                case Themes.Light: return false;
                default:
                    // No host theme (or Themes.None): fall back to the Windows app theme.
                    try
                    {
                        var v = Microsoft.Win32.Registry.GetValue(
                            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                            "AppsUseLightTheme", 1);
                        return v is int light && light == 0;
                    }
                    catch
                    {
                        return true;
                    }
            }
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void OnContextPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ContextObject.Theme) || _unloaded) return;
            ApplyTheme();
        }

        // ------------------------------------------------------------------ auto-play

        /// <summary>
        /// Start playing as soon as a preview opens. A property of the plugin, not of a file, so
        /// it is one checkbox for everything and it is remembered across restarts.
        /// </summary>
        private void OnAutoPlayChanged(object sender, RoutedEventArgs e)
        {
            if (_settingAutoPlay) return;

            _autoPlay = AutoPlayCheck.IsChecked == true;
            try
            {
                SettingHelper.Set(AutoPlayKey, _autoPlay, SettingsDomain);
            }
            catch
            {
                // A settings write must never take the preview down with it.
            }
        }

        private static bool LoadAutoPlay()
        {
            try { return SettingHelper.Get(AutoPlayKey, false, SettingsDomain); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ load

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            if (_unloaded) return;         // closed before it was ever shown; _cts is disposed
            var token = _cts.Token;

            // The playback reader and the waveform scan each open the file with their own
            // reader, so they run side by side: an MP3 reader indexes every frame when it
            // opens, and the scan should not wait for the other reader's index to finish.
            await Task.WhenAll(OpenPlaybackStreamAsync(token), ScanWaveformAsync(token));
        }

        private async Task OpenPlaybackStreamAsync(CancellationToken token)
        {
            WaveStream stream = null;
            try
            {
                // Opening the reader blocks (an MP3 reader indexes the whole file here), so it
                // runs off the UI thread.
                stream = await Task.Run(() => AudioReaderFactory.Create(_path), token);
                if (_unloaded || token.IsCancellationRequested)
                {
                    stream.Dispose();
                    return;
                }

                _durationSeconds = stream.TotalTime.TotalSeconds;
                UpdateTimeText(0, force: true);
                FormatText.Text = AudioFormatInfo.Describe(stream.WaveFormat);
                _stream = stream;          // kept until the first play, then owned by the player
                stream = null;
                SyncPlayButton();          // the file is playable now

                // Start playing on open when the checkbox says so. The setting is the user's,
                // read once so every preview in the session agrees.
                if (_autoPlay && EnsurePlayer()) PlayFrom(0);
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                return;
            }
            catch (AudioFormatException ex)
            {
                stream?.Dispose();
                _openFailed = true;
                if (!_unloaded) ShowStatus(ex.Message);
                SyncPlayButton();
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                _openFailed = true;
                if (!_unloaded) ShowStatus("Could not open this audio file. " + ex.Message);
                SyncPlayButton();
            }
        }

        /// <summary>
        /// The waveform scan: its own reader, decoded once, delivered progressively so the
        /// preview fills in from left to right instead of staying empty.
        /// </summary>
        private async Task ScanWaveformAsync(CancellationToken token)
        {
            try
            {
                WaveformData result = null;
                bool pending = false;
                bool finalized = false;

                await Task.Run(() =>
                {
                    // A long compressed file keeps a core busy decoding for seconds; it must
                    // not compete with the UI or the audio thread for it. The pool thread's
                    // priority is put back before it returns to the pool.
                    var thread = Thread.CurrentThread;
                    var priority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    try
                    {
                        using (var scan = AudioReaderFactory.Create(_path))
                        {
                            result = WaveformData.Compute(scan, ScanColumns, snapshot =>
                            {
                                // One pending delivery at a time: a slow UI thread must not
                                // queue snapshots. The final result below is delivered regardless.
                                if (pending) return;
                                pending = true;
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    pending = false;
                                    if (finalized || _unloaded) return;
                                    ShowWaveform(snapshot, done: false);
                                }));
                            }, progressSteps: 16, token: token);
                        }
                    }
                    finally
                    {
                        thread.Priority = priority;
                    }
                }, token);

                if (_unloaded || token.IsCancellationRequested) return;
                finalized = true;
                ShowWaveform(result, done: true);
            }
            catch (OperationCanceledException)
            {
                // Preview closed mid-scan.
            }
            catch (Exception ex)
            {
                // The open runs alongside and reports the same broken file more precisely.
                if (!_unloaded && !_openFailed) ShowStatus("Waveform unavailable: " + ex.Message);
            }
        }

        private void ShowWaveform(WaveformData data, bool done)
        {
            Waveform.Data = data;
            if (done && !_openFailed && !_playerFailed) HideStatus();
            UpdatePlayhead(_lastFraction);
        }

        // ------------------------------------------------------------------ status

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

        // ------------------------------------------------------------------ playback

        /// <summary>
        /// Create the WASAPI output on first use and hand it the reader. Returns false when
        /// this file cannot be played (no output device, rejected format) — the waveform still
        /// shows, it just cannot be played.
        /// </summary>
        private bool EnsurePlayer()
        {
            if (_player != null) return true;
            if (_playerFailed || _stream == null) return false;

            try
            {
                var stream = _stream;
                _stream = null;            // the player owns (and on failure disposes) it
                _player = new AudioPlayer(stream);
                _player.Volume = _volume;  // the level chosen before anything was playing
                _player.PlaybackStopped += OnPlaybackStopped;
                return true;
            }
            catch (Exception ex)
            {
                _playerFailed = true;      // do not retry on every click
                ShowStatus("Playback unavailable: " + ex.Message);
                SyncPlayButton();
                return false;
            }
        }

        /// <summary>
        /// Start playing from <paramref name="fromSeconds"/>, discarding whatever the output
        /// had buffered for the old position.
        /// </summary>
        private void PlayFrom(double fromSeconds)
        {
            if (_player == null) return;
            try
            {
                _player.SeekAndPlay(TimeSpan.FromSeconds(fromSeconds));
            }
            catch (Exception ex)
            {
                // The output is bound to the device it was created on; once that device is
                // gone (headphones unplugged, Bluetooth dropped) every Play throws.
                DropPlayer("Playback stopped: the audio device is no longer available. "
                    + "Reopen the preview to play on the current device. (" + ex.Message + ")");
                return;
            }
            AnchorClock(fromSeconds);
            _idleTicks = 0;
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(TickMilliseconds)
                };
                _timer.Tick += (s, a) => Tick();
            }
            _timer.Start();
            Playhead.Visibility = Visibility.Visible;
            SyncPlayButton();
        }

        // ------------------------------------------------------------------ play / pause

        private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayback();

        /// <summary>
        /// Button behaviour: while playing it pauses, otherwise it plays from wherever the
        /// playhead is. At the end of the file, playing starts over rather than doing nothing.
        /// </summary>
        private void TogglePlayback()
        {
            if (_player == null && !EnsurePlayer()) return;

            if (_player.IsPlaying)
            {
                PausePlayback();
                return;
            }

            double seconds = _lastFraction >= 0.999 ? 0 : _lastFraction * _durationSeconds;
            PlayFrom(seconds);
        }

        /// <summary>
        /// Hold the output where it is. The clock is stopped with it, so the playhead stays on
        /// the frame it was showing instead of drifting on while silent.
        /// </summary>
        private void PausePlayback()
        {
            try { _player?.Pause(); } catch { /* a dead device is reported on the next play */ }
            _timer?.Stop();
            _clock.Stop();
            SyncPlayButton();
        }

        /// <summary>
        /// Point the button at the current state: the glyph follows playback, and the button is
        /// only enabled when this file can actually be played.
        /// </summary>
        private void SyncPlayButton()
        {
            bool playable = !_playerFailed && !_openFailed && (_player != null || _stream != null);
            PlayPauseButton.IsEnabled = playable;

            bool playing = _player?.IsPlaying == true;
            if (playing == _buttonShowsPause) return;
            _buttonShowsPause = playing;
            PlayIcon.Data = playing ? PauseGeometry : PlayGeometry;
        }

        // Icon geometry rather than Segoe MDL2 glyphs: no font dependency, and it stays crisp at
        // any DPI. Both are 9 x 12 so the button does not shift when the glyph changes.
        private static readonly Geometry PlayGeometry = Freeze(Geometry.Parse("M 0,0 L 9,6 L 0,12 Z"));
        private static readonly Geometry PauseGeometry =
            Freeze(Geometry.Parse("M 0,0 L 3,0 L 3,12 L 0,12 Z M 6,0 L 9,0 L 9,12 L 6,12 Z"));

        private static Geometry Freeze(Geometry geometry)
        {
            geometry.Freeze();
            return geometry;
        }

        /// <summary>Silence the output while a drag is in progress; the release plays again.</summary>
        private void SilenceForScrub()
        {
            try { _player?.Pause(); } catch { /* a dead device is reported on the next play */ }
            _timer?.Stop();
            _clock.Stop();
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // Raised for the end of the file too; only a failure is worth telling the user.
            if (e.Exception == null || _unloaded) return;
            DropPlayer("Playback stopped: " + e.Exception.Message);
        }

        /// <summary>
        /// Give up on playback for this preview after the output failed, and say why. The
        /// waveform stays; clicks no longer try to play.
        /// </summary>
        private void DropPlayer(string message)
        {
            _timer?.Stop();
            _clock.Stop();
            if (_player != null)
            {
                _player.PlaybackStopped -= OnPlaybackStopped;
                try { _player.Dispose(); } catch { }
                _player = null;
            }
            _playerFailed = true;
            ShowStatus(message);
            SyncPlayButton();
        }

        /// <summary>
        /// Drives the playhead and the clock from a monotonic stopwatch anchored at the last
        /// seek, not from the output's position.
        /// </summary>
        /// <remarks>
        /// <see cref="AudioPlayer.Position"/> is the reader's position, which runs ahead of
        /// what is audible by the output buffer and can step when the device changes state.
        /// Feeding it straight to the playhead made the playhead jump at the drop of a hat —
        /// most visibly right after a click, where it first honoured the click and was then
        /// yanked back to wherever the audio still was. The clock only ever moves forwards;
        /// the output's position is used solely to correct real drift.
        /// </remarks>
        private void Tick()
        {
            if (_player == null || _unloaded || _scrubbing)
            {
                return;
            }

            double total = _player.Duration.TotalSeconds;
            double played = _anchorSeconds + _clock.Elapsed.TotalSeconds;
            double actual = _player.Position.TotalSeconds;
            if (Math.Abs(actual - played) > 0.3)
            {
                // Real drift (device stall, a seek we did not make): trust the audio again.
                AnchorClock(actual);
                played = actual;
            }
            if (total > 0 && played > total) played = total;

            _lastFraction = total > 0 ? played / total : 0;
            UpdatePlayhead(_lastFraction);
            UpdateTimeText(played, force: false);

            if (_player.IsPlaying)
            {
                _idleTicks = 0;
                return;
            }

            // The output reports "not playing" for a moment while starting, and permanently
            // at the end of the file or when the device disappears: stop after a short grace.
            if (++_idleTicks > 10)
            {
                _timer?.Stop();
                _clock.Stop();
                SyncPlayButton();      // back to "播放": the file ended, or the device went away
            }
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Also fires while the control seeds itself from _volume (and would fire if markup
            // ever set an initial Value): that is not a user adjustment, so it must not write
            // the remembered level.
            if (_settingVolume) return;

            _volume = (float)e.NewValue;
            if (_player != null) _player.Volume = _volume;
            ScheduleVolumeSave();
        }

        /// <summary>
        /// Persist the level once the user stops moving the slider. Writing per change is what
        /// makes a slider drag janky: SettingHelper rewrites the whole XML document on every
        /// Set, and a drag fires this dozens of times a second.
        /// </summary>
        private void ScheduleVolumeSave()
        {
            _volumeDirty = true;
            if (_volumeSaveTimer == null)
            {
                _volumeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _volumeSaveTimer.Tick += (s, e) =>
                {
                    _volumeSaveTimer.Stop();
                    SaveVolume();
                };
            }

            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }

        private void SaveVolume()
        {
            if (!_volumeDirty) return;
            _volumeDirty = false;
            try
            {
                SettingHelper.Set(VolumeKey, (double)_volume, SettingsDomain);
            }
            catch
            {
                // A settings write must never take the preview down with it.
            }
        }

        private static float LoadVolume()
        {
            try
            {
                double stored = SettingHelper.Get(VolumeKey, DefaultVolume, SettingsDomain);
                return (float)Math.Max(0.0, Math.Min(1.0, stored));
            }
            catch
            {
                return (float)DefaultVolume;
            }
        }

        /// <summary>Restart the playhead clock at the given audio position.</summary>
        private void AnchorClock(double seconds)
        {
            _anchorSeconds = seconds;
            _clock.Restart();
        }

        private void UpdatePlayhead(double fraction)
        {
            double width = Waveform.ActualWidth;
            if (width <= 0) return;
            PlayheadShift.X = Clamp01(fraction) * width;
        }

        /// <summary>
        /// Hide the format line on a narrow preview. The clock is pinned to the left edge and
        /// the format line to the play button's left, so below roughly 520 px the two would
        /// draw over each other.
        /// </summary>
        private void UpdateFormatVisibility()
        {
            FormatText.Visibility = ActualWidth >= 520 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

        private void UpdateTimeText(double positionSeconds, bool force)
        {
            int second = (int)positionSeconds;
            if (!force && second == _lastSecond) return;
            _lastSecond = second;
            TimeText.Text = Format(positionSeconds) + " / " + Format(_durationSeconds);
        }

        /// <summary>mm:ss, or h:mm:ss once the duration needs hours.</summary>
        internal static string Format(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        // ------------------------------------------------------------------ seeking

        private void OnAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            // No Focus(): QuickLook does not take focus from Explorer, and the preview handles
            // no keys, so there is nothing to focus for.
            if (!EnsurePlayer()) return;

            _scrubbing = true;
            Area.CaptureMouse();
            // Silence the output while the pointer moves; the release plays from where it lands.
            SilenceForScrub();
            Playhead.Visibility = Visibility.Visible;
            MovePlayheadToPointer(e);
            e.Handled = true;
        }

        private void OnAreaMouseMove(object sender, MouseEventArgs e)
        {
            if (_scrubbing) MovePlayheadToPointer(e);
        }

        private void OnAreaMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            MovePlayheadToPointer(e);
            EndScrub();
            e.Handled = true;
        }

        /// <summary>
        /// Capture taken away mid-drag (Alt-Tab, a window popping up): finish the gesture at
        /// the last position instead of leaving the preview stuck in a drag that never ends.
        /// </summary>
        private void OnAreaLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_scrubbing) EndScrub();
        }

        private void EndScrub()
        {
            _scrubbing = false;            // before the release, which raises LostMouseCapture
            Area.ReleaseMouseCapture();
            // The only seek of the whole gesture. SeekAndPlay stops the output first, which
            // both drops its stale buffer and ends the playback thread's reads, so the reader
            // is never repositioned while another thread is reading from it.
            PlayFrom(_lastFraction * _durationSeconds);
        }

        /// <summary>Move the playhead and the clock to the pointer. The reader is not touched.</summary>
        private void MovePlayheadToPointer(MouseEventArgs e)
        {
            double width = Waveform.ActualWidth;
            if (width > 0)
            {
                // Coordinates are taken in the waveform's space, so the preview's margins are
                // accounted for automatically and a drag past either edge clamps.
                double x = e.GetPosition(Waveform).X;
                _lastFraction = Clamp01(x / width);
            }

            UpdatePlayhead(_lastFraction);
            UpdateTimeText(_lastFraction * _durationSeconds, force: true);
        }

        // ------------------------------------------------------------------ teardown

        public void Unload()
        {
            _unloaded = true;              // stop every in-flight continuation from touching us
            _cts.Cancel();                 // abort an in-flight decode
            _timer?.Stop();
            _timer = null;
            // Closing within the debounce window must not lose the level.
            _volumeSaveTimer?.Stop();
            _volumeSaveTimer = null;
            SaveVolume();
            if (_context != null) _context.PropertyChanged -= OnContextPropertyChanged;
            _player?.Dispose();            // releases the WASAPI device and the file handle
            _player = null;
            _stream?.Dispose();            // never played: the reader still holds the file
            _stream = null;
            _cts.Dispose();
        }
    }
}
