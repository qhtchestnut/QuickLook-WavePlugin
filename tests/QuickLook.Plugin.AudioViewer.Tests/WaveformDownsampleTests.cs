using System;
using System.Linq;
using QuickLook.Plugin.AudioViewer.Audio;
using Xunit;

namespace QuickLook.Plugin.AudioViewer.Tests
{
    /// <summary>
    /// The fitted view squeezes up to 12000 columns into a pane a few hundred pixels wide.
    /// Downsample must keep every peak, or a transient vanishes when the user zooms out.
    /// </summary>
    public class WaveformDownsampleTests
    {
        [Fact]
        public void Downsample_EvenSpans()
        {
            var wf = WaveformData.FromChannels(
                new[] { new[] { -1f, -2f, -3f, -4f } },
                new[] { new[] { 1f, 2f, 3f, 4f } });

            var d = wf.Downsample(2);

            Assert.Equal(new[] { -2f, -4f }, d.Min[0]);
            Assert.Equal(new[] { 2f, 4f }, d.Max[0]);
        }

        [Fact]
        public void Downsample_SingleSpikeSurvives()
        {
            var min = Enumerable.Repeat(-0.01f, 1000).ToArray();
            var max = Enumerable.Repeat(0.01f, 1000).ToArray();
            max[537] = 0.9f;
            var wf = WaveformData.FromChannels(new[] { min }, new[] { max });

            var d = wf.Downsample(10);

            Assert.Equal(0.9f, d.Max[0][5]);
        }

        [Fact]
        public void Downsample_UnevenSpans_PreservesGlobalPeaks()
        {
            var rnd = new Random(1);
            var min = new float[12000];
            var max = new float[12000];
            for (int i = 0; i < min.Length; i++)
            {
                min[i] = -(float)rnd.NextDouble();
                max[i] = (float)rnd.NextDouble();
            }
            var wf = WaveformData.FromChannels(new[] { min }, new[] { max });

            var d = wf.Downsample(861);

            Assert.Equal(861, d.Columns);
            Assert.Equal(min.Min(), d.Min[0].Min());
            Assert.Equal(max.Max(), d.Max[0].Max());
        }

        [Fact]
        public void Downsample_CombinedArraysReduced()
        {
            var wf = WaveformData.FromChannels(
                new[] { new[] { -1f, -1f, -1f, -1f }, new[] { -1f, -1f, -1f, -1f } },
                new[] { new[] { 1f, 1f, 1f, 1f }, new[] { 1f, 1f, 1f, 1f } },
                combinedMin: new[] { -0.1f, -0.5f, -0.2f, -0.3f },
                combinedMax: new[] { 0.6f, 0.1f, 0.2f, 0.7f });

            var d = wf.Downsample(2);

            Assert.Equal(new[] { -0.5f, -0.3f }, d.CombinedMin);
            Assert.Equal(new[] { 0.6f, 0.7f }, d.CombinedMax);
        }

        [Fact]
        public void Downsample_CombinedNull_StaysNull()
        {
            var wf = WaveformData.FromChannels(
                new[] { new[] { -1f, -2f, -3f, -4f } },
                new[] { new[] { 1f, 2f, 3f, 4f } });

            var d = wf.Downsample(2);

            Assert.Null(d.CombinedMin);
            Assert.Null(d.CombinedMax);
        }

        [Fact]
        public void Downsample_NotSmaller_ReturnsSameInstance()
        {
            var wf = WaveformData.FromChannels(
                new[] { new[] { -1f, -2f } },
                new[] { new[] { 1f, 2f } });

            Assert.Same(wf, wf.Downsample(wf.Columns));
        }
    }
}
