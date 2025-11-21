using System;

namespace GeminiLiveConsole;

public static class PcmEncoder
{
    public static byte[] FloatTo16BitPcm(ReadOnlySpan<float> samples)
    {
        var buffer = new byte[samples.Length * 2];
        int o = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            short s = (short)(v < 0 ? v * 32768 : v * 32767);
            buffer[o++] = (byte)(s & 0xFF);
            buffer[o++] = (byte)((s >> 8) & 0xFF);
        }
        return buffer;
    }
}
