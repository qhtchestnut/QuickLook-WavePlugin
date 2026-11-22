using NAudio.Dmo;
using NAudio.Wave;
using QuickLook.Plugin.AudioViewer.Audio;
using Xunit;

namespace QuickLook.Plugin.AudioViewer.Tests
{
    /// <summary>
    /// The footer's format line: sample rate in kHz, bit depth in bits, and an F for
    /// floating-point samples.
    /// </summary>
    public class AudioFormatInfoTests
    {
        [Theory]
        [InlineData(44100, 16, false, "44.1kHz · 16bit")]
        [InlineData(48000, 24, false, "48kHz · 24bit")]
        [InlineData(96000, 32, true, "96kHz · 32bit F")]
        [InlineData(22050, 16, false, "22.05kHz · 16bit")]
        [InlineData(11025, 8, false, "11.025kHz · 8bit")]
        public void Describe_matches_the_expected_line(int rate, int bits, bool isFloat, string expected)
            => Assert.Equal(expected, AudioFormatInfo.Describe(rate, bits, isFloat));

        [Fact]
        public void Describe_drops_the_bit_depth_when_the_format_has_none()
        {
            // Compressed sources report no bit depth; the rate alone is the honest answer.
            Assert.Equal("44.1kHz", AudioFormatInfo.Describe(44100, 0, false));
        }

        [Fact]
        public void Describe_handles_an_unknown_rate()
        {
            Assert.Equal("16bit", AudioFormatInfo.Describe(0, 16, false));
            Assert.Equal(string.Empty, AudioFormatInfo.Describe(0, 0, false));
            Assert.Equal(string.Empty, AudioFormatInfo.Describe(null));
        }

        [Fact]
        public void IsFloat_recognises_plain_and_extensible_formats()
        {
            // NAudio picks the SubFormat from the bit depth: 32-bit extensible is float,
            // 16-bit is integer PCM.
            Assert.True(AudioFormatInfo.IsFloat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)));
            Assert.True(AudioFormatInfo.IsFloat(new WaveFormatExtensible(48000, 32, 2)));
            Assert.False(AudioFormatInfo.IsFloat(new WaveFormatExtensible(48000, 16, 2)));
            Assert.False(AudioFormatInfo.IsFloat(new WaveFormat(44100, 16, 2)));
            Assert.False(AudioFormatInfo.IsFloat(null));
        }

        [Fact]
        public void Describe_reads_a_wave_format_end_to_end()
        {
            var format = new WaveFormat(44100, 16, 2);
            Assert.Equal("44.1kHz · 16bit", AudioFormatInfo.Describe(format));
        }
    }
}
