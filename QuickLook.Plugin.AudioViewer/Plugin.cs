using System;
using System.IO;
using System.Linq;
using System.Windows;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.AudioViewer
{
    /// <summary>
    /// Audio preview: draws the whole file's waveform fitted to the window and plays it.
    /// Nothing else — no spectrogram, no zoom, no scrolling, no settings.
    /// </summary>
    public class Plugin : IViewer
    {
        public static readonly string[] SupportedExtensions =
        {
            ".wav", ".mp3", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".wma", ".aif", ".aiff"
        };

        private WaveformPreviewControl _control;

        public int Priority => 0;

        public void Init()
        {
        }

        public bool CanHandle(string path)
        {
            if (Directory.Exists(path))
                return false;

            var ext = Path.GetExtension(path);
            return SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        public void Prepare(string path, ContextObject context)
        {
            // A single waveform strip needs no more room than this; the user can resize
            // QuickLook's window and the waveform re-fits itself.
            context.PreferredSize = new Size(720, 240);
        }

        public void View(string path, ContextObject context)
        {
            context.Title = Path.GetFileName(path);
            _control = new WaveformPreviewControl(path, context);   // self-loads async
            context.ViewerContent = _control;
            context.IsBusy = false;                                 // the control draws its own progress
        }

        public void Cleanup()
        {
            _control?.Unload();
            _control = null;
        }
    }
}
