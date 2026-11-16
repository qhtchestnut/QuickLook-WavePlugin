using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;

namespace QuickLook.Plugin.AudioViewer.Audio
{
    /// <summary>
    /// Creates a seekable NAudio WaveStream for a supported audio file,
    /// preferring native readers and falling back to Media Foundation.
    /// </summary>
    /// <remarks>
    /// Every reader is built on a stream this factory opens itself, with read, write and
    /// delete sharing. NAudio's path constructors use <see cref="File.OpenRead(string)"/>,
    /// which shares read only: with a preview open, renaming or deleting the file in
    /// Explorer then fails with "the file is in use". QuickLook stays open beside Explorer,
    /// so that is an everyday action, not an edge case.
    /// </remarks>
    public static class AudioReaderFactory
    {
        public static WaveStream Create(string path)
        {
            if (!File.Exists(path))
                throw new AudioFormatException($"File not found: {path}");

            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            Stream file = null;
            try
            {
                file = OpenShared(path);
                WaveStream reader;
                switch (ext)
                {
                    case ".wav":
                        reader = new WaveFileReader(file);
                        break;
                    case ".mp3":
                        reader = new Mp3FileReader(file);
                        break;
                    case ".aif":
                    case ".aiff":
                        // NAudio's AiffFileReader handles plain PCM AIFF but throws on
                        // AIFF-C (e.g. "fl32" float). Route AIFC to our own reader by peeking
                        // the FORM type.
                        reader = IsAifc(file) ? (WaveStream)new AiffReader(file) : new AiffFileReader(file);
                        break;
                    case ".ogg":
                    case ".oga":
                        reader = new VorbisWaveReader(file, false);
                        break;
                    case ".flac":
                    case ".m4a":
                    case ".aac":
                    case ".wma":
                        reader = new StreamMediaFoundationReader(file);
                        break;
                    default:
                        throw new AudioFormatException($"Unsupported audio format: {ext}");
                }

                return new FileOwningWaveStream(reader, file);
            }
            catch (AudioFormatException)
            {
                file?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                file?.Dispose();
                throw new AudioFormatException(
                    $"Could not open audio file (unsupported or corrupt): {Path.GetFileName(path)}", ex);
            }
        }

        /// <summary>
        /// Open for reading without stopping anyone else from writing, renaming or deleting
        /// the file. A delete while open succeeds; the data stays readable until we close.
        /// </summary>
        internal static FileStream OpenShared(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

        // Peek the FORM type: AIFF-C ("AIFC") needs our reader; plain "AIFF" uses NAudio's.
        private static bool IsAifc(Stream file)
        {
            try
            {
                var b = new byte[12];
                int n = file.Read(b, 0, 12);
                return n == 12 && b[0] == 'F' && b[1] == 'O' && b[2] == 'R' && b[3] == 'M'
                    && b[8] == 'A' && b[9] == 'I' && b[10] == 'F' && b[11] == 'C';
            }
            catch
            {
                return false;
            }
            finally
            {
                file.Position = 0;
            }
        }

        /// <summary>
        /// The reader, plus ownership of the file stream it reads from. NAudio's stream
        /// constructors leave the stream open on dispose, so without this the handle leaks.
        /// </summary>
        private sealed class FileOwningWaveStream : WaveStream
        {
            private readonly WaveStream _inner;
            private Stream _file;

            public FileOwningWaveStream(WaveStream inner, Stream file)
            {
                _inner = inner;
                _file = file;
            }

            public override WaveFormat WaveFormat => _inner.WaveFormat;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override TimeSpan CurrentTime
            {
                get => _inner.CurrentTime;
                set => _inner.CurrentTime = value;
            }

            public override TimeSpan TotalTime => _inner.TotalTime;

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _file?.Dispose();
                    _file = null;
                }
                base.Dispose(disposing);
            }
        }
    }
}
