using System;

namespace questline.Client;

/// <summary>
/// Rolling frame timings for the canvas: whole-frame time from the game's delta, and the
/// time spent inside the canvas's own render, over the last <see cref="Capacity"/> frames.
/// </summary>
public class CanvasFrameStats
{
    public const int Capacity = 3600;

    readonly float[] frameMs = new float[Capacity];
    readonly float[] canvasMs = new float[Capacity];
    int next, count;

    public int Count => count;

    public void Record(float frame, double canvas)
    {
        frameMs[next] = frame;
        canvasMs[next] = (float)canvas;
        next = (next + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
    }

    public void Reset() => next = count = 0;

    public float AvgFrameMs => Average(frameMs);
    public float AvgCanvasMs => Average(canvasMs);
    public float MaxFrameMs => Percentile(frameMs, 1);
    public float MaxCanvasMs => Percentile(canvasMs, 1);
    public float P95FrameMs => Percentile(frameMs, 0.95f);
    public float P95CanvasMs => Percentile(canvasMs, 0.95f);

    float Average(float[] samples)
    {
        if (count == 0) return 0;
        float sum = 0;
        for (int i = 0; i < count; i++) sum += samples[i];
        return sum / count;
    }

    float Percentile(float[] samples, float p)
    {
        if (count == 0) return 0;
        var sorted = samples[..count];
        Array.Sort(sorted);
        return sorted[Math.Min(count - 1, (int)(p * count))];
    }

    public override string ToString() =>
        $"frame {AvgFrameMs:0.0} ms avg, {P95FrameMs:0.0} p95, {MaxFrameMs:0.0} max · " +
        $"canvas {AvgCanvasMs:0.00} ms avg, {P95CanvasMs:0.00} p95, {MaxCanvasMs:0.00} max · {count} frames";
}
