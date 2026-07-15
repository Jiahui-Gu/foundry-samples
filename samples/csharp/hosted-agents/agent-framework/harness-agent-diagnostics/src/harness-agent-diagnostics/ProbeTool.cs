using System.Collections.ObjectModel;

namespace HarnessAgentDiagnostics;

public static class ProbeTool
{
    public static ProbeResult ComputeProbe(string label, int[] values)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Label must not be blank.", nameof(label));
        }

        if (values is null || values.Length == 0)
        {
            throw new ArgumentException("Values must not be null or empty.", nameof(values));
        }

        int[] orderedValues = [.. values];
        Array.Sort(orderedValues);

        int sum = 0;
        foreach (int value in orderedValues)
        {
            sum += value;
        }

        return new ProbeResult(label, Array.AsReadOnly(orderedValues), sum);
    }
}

public sealed class ProbeResult
{
    public ProbeResult(string label, ReadOnlyCollection<int> orderedValues, int sum)
    {
        Label = label;
        OrderedValues = orderedValues;
        Sum = sum;
    }

    public string Label { get; }

    public IReadOnlyList<int> OrderedValues { get; }

    public int Sum { get; }
}
