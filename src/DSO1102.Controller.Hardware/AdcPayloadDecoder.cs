using System.Buffers.Binary;

namespace DSO1102.Controller.Hardware;

internal static class AdcPayloadDecoder
{
    internal sealed record Result(double[] Samples, int ReplacedCount);

    internal static Result Decode(string? base64, int count)
    {
        if (count is < 32 or > 524_288)
            throw new InvalidOperationException("ADC sample count must be 32..524288.");
        var expectedBytes = checked(count * sizeof(ushort));
        var expectedBase64Length = ((expectedBytes + 2) / 3) * 4;
        if (base64 is null || base64.Length != expectedBase64Length)
            throw new InvalidOperationException("ADC payload length does not match the declared count.");
        var bytes = new byte[expectedBytes];
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || written != expectedBytes)
            throw new InvalidOperationException("ADC payload is not exact u16le-base64 data.");

        var samples = new double[count];
        var firstValid = -1;
        var invalid = 0;
        for (var i = 0; i < count; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            samples[i] = value;
            if (value > 255) invalid++;
            else if (firstValid < 0) firstValid = i;
        }
        // Apply the existing bridge's 95% criterion to the ENTIRE record,
        // rather than only its 10,240-sample diagnostic prefix.
        if (firstValid < 0 || invalid > count * 0.05)
            throw new InvalidOperationException("More than 5% of ADC samples are invalid; frame rejected.");

        var previous = samples[firstValid];
        for (var i = 0; i < count; i++)
        {
            if (samples[i] <= 255) previous = samples[i];
            samples[i] = previous;
        }
        return new(samples, invalid);
    }
}
