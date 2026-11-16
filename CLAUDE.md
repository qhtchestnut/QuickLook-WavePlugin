# 工程笔记（给接手的人 / AI 助手）

> 这份文件是给改代码的人看的：目录结构、容易踩的坑、以及改动为什么要这么写。
> 面向使用者的说明在 [README.md](README.md)。内容用英文，保持与代码注释一致。

**QuickLook.Plugin.AudioViewer** —— 一条整段波形 + 点击跳转播放。C# / WPF / .NET Framework 4.8.

This is a deliberate rewrite of the 1.x plugin (which had a spectrogram, time
zoom, deep-zoom tiles, a note layer and a settings flyout). **Do not add
features back without asking**: the point of 2.0 is that opening a file and
playing it costs almost nothing, and every feature 1.x had was paid for on every
preview.

## Build and test

```
dotnet build QuickLook.Plugin.AudioViewer/QuickLook.Plugin.AudioViewer.csproj -c Release
powershell -File Scripts/test.ps1
powershell -File Scripts/pack-zip.ps1
```

`Scripts/test.ps1` builds, then copies the test output to `%TEMP%` and runs it
there. That staging is not optional on some machines: .NET Framework resolves
test assemblies with `Assembly.LoadFrom` and can refuse an assembly under the
repo as "loaded from a network location" (HRESULT 0x80131515), which makes VSTest
report that the adapter could not be loaded. Plain `dotnet test` works where
that does not happen.

`QuickLook.Common` is a submodule (present as source here). It targets net462
and its csproj carries a `PreBuildEvent` that needs upstream's solution;
`Directory.Build.targets` clears it for that project. Reference assemblies for
both target frameworks come from the `Microsoft.NETFramework.ReferenceAssemblies`
package in `Directory.Build.props`, so no Visual Studio or targeting pack is
needed.

## Layout

- `QuickLook.Plugin.AudioViewer/Plugin.cs` — `IViewer`: extensions, preferred
  size (720×240), and creating the control.
- `WaveformPreviewControl.xaml(.cs)` — the whole preview: theme, load, seek,
  playback, clock, teardown. ~400 lines, on purpose.
- `Controls/WaveformView.cs` — rasterizes a `WaveformData` to a bitmap at the
  element's own pixel size and blits it.
- `Audio/` — unchanged, proven 1.x code: `WaveformData` (streaming peak scan with
  progressive snapshots), `WaveformRasterizer`, `AudioReaderFactory`,
  `AudioPlayer` (WASAPI), `AiffReader`, `ChannelMixSampleProvider`.
- `tools/WaveformHarness` — dev tool: opens a real file in a real window and
  prints open/scan/paint/idle-CPU/tick numbers. `--make-test` writes its own WAV,
  `--screenshot` renders a PNG, `--play` exercises playback. Not shipped.

## Invariants that are easy to break

- **Never repaint the waveform to move the playhead.** `WaveformView` caches a
  frozen bitmap keyed on (data reference, lane pixel height, column count,
  colour); the playhead is a separate `Rectangle` moved with a
  `TranslateTransform`. 1.x invalidated the whole waveform visual on every 33 ms
  tick, even while paused, which is what made playback and idling feel heavy.
- **The playhead is driven by a stopwatch, not by `AudioPlayer.Position`.** That
  position is the reader's, which runs ahead by the output buffer and steps when
  the device changes state; feeding it to the playhead made it jump right after a
  seek. `Tick` advances `_anchorSeconds + _clock.Elapsed` and only re-anchors when
  the reader disagrees by more than 0.3 s. It also returns early while
  `_scrubbing`, so a drag owns the playhead.
- **An audible seek goes through `AudioPlayer.SeekAndPlay`.** `WasapiOut` keeps a
  full buffer (~100 ms) of audio read from the old position; `Stop()` is the only
  call that drops it (`audioClient.Reset`), and `Pause()` explicitly keeps it.
  Seek a paused output with plain `Seek`, never under a running one.
- **The timer only runs while playing.** `Tick` stops it at the end of the file
  and `PausePlayback` stops it; do not start it from load.
- **Do not create the `AudioPlayer` on load.** An initialised WASAPI stream that
  is never played still costs CPU on its own thread (measured: 7.8 % of a core).
  `EnsurePlayer()` exists for exactly this reason and hands over `_stream`, which
  the player then owns and disposes. It also applies `_volume`, so a level chosen
  before anything played is not lost.
- **The volume slider is a sibling of the seek surface, not a child.** Put it
  inside `Area` and dragging it would also seek.
- **Volume lives in a static and the slider must not carry a `Value` in markup.**
  The control seeds the slider from `_volume` behind a `_settingVolume` guard;
  an initial `Value="0.8"` in the XAML fires `ValueChanged` while loading and
  writes 0.8 back into the static, which silently reset the level on every new
  preview. `IsMoveToPointEnabled="True"` is deliberate too: without it a click
  steps by `LargeChange` instead of landing where it was clicked.
- **Volume is persisted, but never per change.** `SettingHelper.Set` rewrites the
  whole XML document, so a slider drag would write dozens of times a second: the
  write is debounced 400 ms and flushed from `Unload()` (a preview closed inside
  the debounce window must not lose the level). The value is stored in the same
  settings file as 1.x used, next to the `AutoPlay` key that is still read.
- **The rasterizer bakes in colour**: the cached bitmap has the wave colour in
  its pixels, so changing `WaveColor` must drop it (the setter does).
- `Unload()` must stop the timer, cancel `_cts`, drop the `ContextObject`
  `PropertyChanged` subscription (the host outlives the preview) and dispose both
  the player and the un-played stream.
- Arrow keys and Space belong to Explorer and QuickLook. Only `K` is handled, and
  `Focus()` is called only from a user click.

## Measurement

`tools/WaveformHarness` is how changes here get justified. Reference numbers for
a 3-minute stereo WAV on the development machine: scan 42 ms, first paint 177 ms
cold / 26 ms warm, idle CPU 0 %, playhead 0.02 ms per tick, playback 3 % of one
core.
