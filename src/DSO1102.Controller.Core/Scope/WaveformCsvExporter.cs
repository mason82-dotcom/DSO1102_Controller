using System.Globalization;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Core.Scope;

public static class WaveformCsvExporter
{
    /// <summary>
    /// Writes time and both channels as CSV (invariant culture, comma separated).
    /// Metadata lines start with '#'. Time is relative to the trigger point.
    /// </summary>
    public static void Write(TextWriter writer, ScopeView view, bool visibleWindowOnly)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(view);

        var frame = view.Frame;
        var unit = frame.SampleDomain == SampleDomain.Volts ? "V" : "ADC";
        var start = visibleWindowOnly ? view.WindowStart : 0;
        var count = visibleWindowOnly ? view.WindowLength : frame.SampleCount;
        var inv = CultureInfo.InvariantCulture;

        writer.WriteLine($"# DSO1102 Controller export");
        writer.WriteLine(string.Create(inv, $"# captured={frame.Timestamp:O}"));
        writer.WriteLine(string.Create(inv, $"# sample_rate_hz={frame.SampleRate:R}"));
        writer.WriteLine($"# sample_domain={frame.SampleDomain}");
        writer.WriteLine($"# amplitude_calibrated={(frame.IsAmplitudeCalibrated ? "true" : "false")}");
        writer.WriteLine(string.Create(inv, $"# replaced_samples_ch1={frame.ReplacedSamplesChannel1}"));
        writer.WriteLine(string.Create(inv, $"# replaced_samples_ch2={frame.ReplacedSamplesChannel2}"));
        writer.WriteLine($"# trigger_state={view.TriggerState}");
        writer.WriteLine($"# range={(visibleWindowOnly ? "visible_window" : "full_record")}");
        writer.WriteLine($"time_s,ch1_{unit},ch2_{unit}");

        for (var i = start; i < start + count; i++)
        {
            writer.Write(view.TimeOfSample(i).ToString("G9", inv));
            writer.Write(',');
            writer.Write(frame.Channel1[i].ToString("G9", inv));
            writer.Write(',');
            writer.WriteLine(frame.Channel2[i].ToString("G9", inv));
        }
    }
}
