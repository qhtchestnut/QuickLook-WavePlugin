using QuickLook.Plugin.AudioViewer;
using Xunit;

namespace QuickLook.Plugin.AudioViewer.Tests
{
    /// <summary>
    /// The transport clock: no milliseconds, and hours only when the duration needs them.
    /// </summary>
    public class TimeFormatTests
    {
        [Theory]
        [InlineData(0.0, "0:00")]
        [InlineData(7.4, "0:07")]
        [InlineData(7.9, "0:07")]      // truncated, not rounded: the clock must not run ahead
        [InlineData(59.99, "0:59")]
        [InlineData(60.0, "1:00")]
        [InlineData(225.0, "3:45")]
        [InlineData(3599.0, "59:59")]
        [InlineData(3600.0, "1:00:00")]
        [InlineData(3661.0, "1:01:01")]
        public void Format_matches_the_expected_clock(double seconds, string expected)
            => Assert.Equal(expected, WaveformPreviewControl.Format(seconds));

        [Fact]
        public void Format_treats_negative_and_nan_as_zero()
        {
            Assert.Equal("0:00", WaveformPreviewControl.Format(-5));
            Assert.Equal("0:00", WaveformPreviewControl.Format(double.NaN));
        }
    }
}
