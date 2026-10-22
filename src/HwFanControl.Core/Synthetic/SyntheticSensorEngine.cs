using HwFanControl.Core.Sensors;

namespace HwFanControl.Core.Synthetic;

/// <summary>Evaluates synthetic temperature nodes (Average / Max / Difference).</summary>
public sealed class SyntheticSensorEngine
{
    private readonly List<SyntheticNode> _nodes = new();

    public IReadOnlyList<SyntheticNode> Nodes => _nodes;

    public void Add(SyntheticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Add(node);
    }

    public void Clear() => _nodes.Clear();

    public int Evaluate(TelemetryBuffer source, Span<float> destination, int writeOffset = 0)
    {
        int written = 0;
        for (int n = 0; n < _nodes.Count; n++)
        {
            int destIndex = writeOffset + written;
            if ((uint)destIndex >= (uint)destination.Length)
                break;

            if (!TryEvaluateNode(_nodes[n], source, out float value))
                continue;

            destination[destIndex] = value;
            written++;
        }

        return written;
    }

    public static bool TryEvaluateNode(SyntheticNode node, TelemetryBuffer source, out float value)
    {
        value = 0f;
        ArgumentNullException.ThrowIfNull(node);

        switch (node.Operation)
        {
            case SyntheticOperation.Average:
            {
                if (node.Sources is { Length: > 0 })
                {
                    float sum = 0;
                    int count = 0;
                    foreach (var slot in node.Sources)
                    {
                        if (!source.TryGet(slot, out float v))
                            continue;
                        sum += v;
                        count++;
                    }

                    if (count == 0)
                        return false;
                    value = sum / count;
                    return true;
                }

                if (!source.TryGet(node.Primary, out float a) || !source.TryGet(node.Secondary, out float b))
                    return false;
                value = (a + b) * 0.5f;
                return true;
            }
            case SyntheticOperation.Max:
            {
                if (node.Sources is { Length: > 0 })
                {
                    float max = float.MinValue;
                    bool any = false;
                    foreach (var slot in node.Sources)
                    {
                        if (!source.TryGet(slot, out float v))
                            continue;
                        if (v > max)
                            max = v;
                        any = true;
                    }

                    if (!any)
                        return false;
                    value = max;
                    return true;
                }

                if (!source.TryGet(node.Primary, out float a) || !source.TryGet(node.Secondary, out float b))
                    return false;
                value = System.Math.Max(a, b);
                return true;
            }
            case SyntheticOperation.Difference:
            {
                if (!source.TryGet(node.Primary, out float a) || !source.TryGet(node.Secondary, out float b))
                    return false;
                value = a - b;
                return true;
            }
            default:
                return false;
        }
    }
}
