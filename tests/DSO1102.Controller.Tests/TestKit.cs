namespace DSO1102.Controller.Tests;

/// <summary>
/// Minimal assertion helpers. A failed check throws and ends its suite; the runner keeps
/// going with the next suite and reports all failures at the end.
/// </summary>
internal static class TestKit
{
    public static int Passed { get; private set; }

    public static void Check(bool condition, string name)
    {
        if (!condition) throw new TestFailure(name);
        Passed++;
        Console.WriteLine($"PASS {name}");
    }

    public static void Near(double actual, double expected, double tolerance, string name) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{name}: {actual:g6}");

    public static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is not TestFailure) { Check(true, name); return; }
        throw new TestFailure($"Expected rejection: {name}");
    }

    public static async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new TestFailure($"Expected {typeof(T).Name}: {name}");
    }

    public static string Encode(ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return Convert.ToBase64String(bytes);
    }
}

internal sealed class TestFailure(string message) : Exception(message);
