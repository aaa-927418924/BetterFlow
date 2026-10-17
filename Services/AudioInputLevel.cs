using System.Buffers.Binary;

namespace BetterFlow.Services;

internal static class AudioInputLevel
{
    public static float GetNormalizedLevel(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / sizeof(short);
        if (sampleCount == 0) return 0;

        double squaredTotal = 0;
        for (var offset = 0; offset < sampleCount * sizeof(short); offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(offset, sizeof(short))) / 32768d;
            squaredTotal += sample * sample;
        }

        var rms = Math.Sqrt(squaredTotal / sampleCount);
        if (rms <= 0.00025) return 0;

        var decibels = 20 * Math.Log10(rms);
        return (float)Math.Clamp((decibels + 68) / 42, 0, 1);
    }
}
