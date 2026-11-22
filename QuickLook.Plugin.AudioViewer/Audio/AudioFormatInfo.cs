using System;
using System.Globalization;
using System.Text;
using NAudio.Dmo;
using NAudio.Wave;

namespace QuickLook.Plugin.AudioViewer.Audio
{
    /// <summary>
    /// One-line description of a stream's format for the preview footer: <c>44.1kHz · 16bit</c>,
    /// or <c>48kHz · 32bit F</c> for floating-point samples.
    /// </summary>
    /// <remarks>
    /// Taken from the decoder's <see cref="WaveFormat"/>, so for compressed sources it describes
    /// what is being played back, not the container's nominal rate — which is what the user is
    /// hearing. Formats with no bit depth (MP3, for instance) show the sample rate alone.
    /// </remarks>
    public static class AudioFormatInfo
    {
        public static string Describe(WaveFormat format) =>
            format == null ? string.Empty : Describe(format.SampleRate, format.BitsPerSample, IsFloat(format));

        internal static string Describe(int sampleRate, int bitsPerSample, bool isFloat)
        {
            var text = new StringBuilder();

            if (sampleRate > 0)
                text.Append((sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)).Append("kHz");

            if (bitsPerSample > 0)
            {
                if (text.Length > 0) text.Append(" · ");
                text.Append(bitsPerSample).Append("bit");
                if (isFloat) text.Append(" F");
            }

            return text.ToString();
        }

        /// <summary>
        /// True for IEEE-float samples, in any of the three shapes NAudio reports a format in:
        /// plain <see cref="WaveFormatEncoding.IeeeFloat"/>, an in-process
        /// <see cref="WaveFormatExtensible"/>, or an on-disk WAVE_FORMAT_EXTENSIBLE parsed into
        /// <see cref="WaveFormatExtraData"/> (the SubFormat GUID is exposed raw there).
        /// </summary>
        public static bool IsFloat(WaveFormat format)
        {
            if (format == null) return false;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
            if (format.Encoding != WaveFormatEncoding.Extensible) return false;

            if (format is WaveFormatExtensible ext)
                return ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

            if (format is WaveFormatExtraData extra && format.ExtraSize >= 22)
            {
                var data = extra.ExtraData;
                if (data != null && data.Length >= 22)
                {
                    var subFormat = new byte[16];
                    Array.Copy(data, 6, subFormat, 0, 16);
                    return new Guid(subFormat) == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
                }
            }

            return false;
        }
    }
}
