using System;
using System.IO;
using QuickLook.Plugin.AudioViewer.Audio;
using Xunit;

namespace QuickLook.Plugin.AudioViewer.Tests
{
    public class AudioReaderFactoryTests
    {
        [Fact]
        public void Create_WavFile_ReportsCorrectFormat()
        {
            var dir = Path.GetTempPath();
            var wav = TestSignals.WriteSineWav(dir, sampleRate: 44100, seconds: 2.0, channels: 2);
            try
            {
                using (var stream = AudioReaderFactory.Create(wav))
                {
                    Assert.Equal(44100, stream.WaveFormat.SampleRate);
                    Assert.Equal(2, stream.WaveFormat.Channels);
                    Assert.InRange(stream.TotalTime.TotalSeconds, 1.9, 2.1);
                }
            }
            finally { File.Delete(wav); }
        }

        [Fact]
        public void Create_SixteenChannelWav_OpensAndStreamsCorrectly()
        {
            var dir = Path.GetTempPath();
            var wav = TestSignals.WriteSineWav(dir, sampleRate: 44100, seconds: 0.5, channels: 16);
            try
            {
                using (var stream = AudioReaderFactory.Create(wav))
                {
                    Assert.Equal(16, stream.WaveFormat.Channels);
                    Assert.InRange(stream.TotalTime.TotalSeconds, 0.45, 0.55);
                }
                using (var scan = AudioReaderFactory.Create(wav))
                {
                    var wf = QuickLook.Plugin.AudioViewer.Audio.WaveformData.Compute(scan, columns: 100);
                    Assert.Equal(16, wf.ChannelCount);
                    Assert.Equal(100, wf.Columns);
                }
            }
            finally { File.Delete(wav); }
        }

        [Fact]
        public void Create_OpenReader_DoesNotBlockRenameOrDelete()
        {
            // A preview stays open beside Explorer: renaming or deleting the file there must
            // not fail with "the file is in use".
            var dir = Path.GetTempPath();
            var wav = TestSignals.WriteSineWav(dir, sampleRate: 44100, seconds: 1.0, channels: 2);
            var renamed = wav + ".renamed";
            try
            {
                using (var stream = AudioReaderFactory.Create(wav))
                {
                    File.Move(wav, renamed);
                    File.Move(renamed, wav);

                    File.Delete(wav);
                    Assert.False(File.Exists(wav));

                    // Already-open data stays readable after the delete.
                    var buffer = new byte[4096];
                    Assert.True(stream.Read(buffer, 0, buffer.Length) > 0);
                }
            }
            finally
            {
                if (File.Exists(wav)) File.Delete(wav);
                if (File.Exists(renamed)) File.Delete(renamed);
            }
        }

        [Fact]
        public void Dispose_ReleasesTheFileHandle()
        {
            // The NAudio readers are built on a stream the factory opens, and they leave that
            // stream open on dispose; the factory's wrapper has to close it.
            var dir = Path.GetTempPath();
            var wav = TestSignals.WriteSineWav(dir, sampleRate: 44100, seconds: 0.5, channels: 1);
            try
            {
                AudioReaderFactory.Create(wav).Dispose();
                using (new FileStream(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            finally { File.Delete(wav); }
        }

        [Fact]
        public void Create_UnsupportedOrCorrupt_ThrowsAudioFormatException()
        {
            var dir = Path.GetTempPath();
            var bogus = Path.Combine(dir, $"bogus_{Guid.NewGuid():N}.wav");
            File.WriteAllText(bogus, "this is not audio");
            try
            {
                Assert.Throws<AudioFormatException>(() => AudioReaderFactory.Create(bogus));
            }
            finally { File.Delete(bogus); }
        }
    }
}
