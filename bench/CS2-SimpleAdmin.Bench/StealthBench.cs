using System.Diagnostics;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;
using CS2_SimpleAdmin_StealthModule;

namespace CS2_SimpleAdmin.Bench;

internal static class StealthBench
{
    internal static void Run(Action<string> line)
    {
        line("## H. Stealth CheckTransmit v1.0.3 vs candidate, managed filter");
        line("| Recipients | Targets | Variant | p50 µs | p95 µs | p99 µs | max µs | B/call |");
        line("|---:|---:|:---|---:|---:|---:|---:|---:|");

        foreach (var recipients in new[] { 3, 16, 24, 32, 64 })
        foreach (var count in new[] { 0, 1, Math.Min(4, recipients), recipients }.Distinct())
        {
            var indices = Enumerable.Range(0, count).Select(i => 31 + i * 83).ToArray();
            var owners = Enumerable.Range(0, count).ToArray();
            var targets = new ObserverTargets();
            for (var i = 0; i < count; i++)
                targets.Set(owners[i], indices[i], (uint)indices[i]);

            var wordsPerRecipient = indices.Length == 0 ? 1 : (indices[^1] >> 5) + 1;
            var bits = new uint[wordsPerRecipient * recipients];
            Measure("v1.0.3", recipients, count, () => ApplyBaseline(bits, wordsPerRecipient, recipients, indices, owners));
            Measure("candidate", recipients, count, () => ApplyCandidate(bits, wordsPerRecipient, recipients, targets));

            void Measure(string label, int players, int observers, Action filter)
            {
                var times = Sample(filter, () => Array.Fill(bits, uint.MaxValue));
                line($"| {players} | {observers} | {label} | {times.P50:F3} | {times.P95:F3} | {times.P99:F3} | {times.Max:F3} | {times.Bytes:F3} |");
            }
        }

        var refresh = new ObserverTargets();
        var single = Sample(() => refresh.Set(0, 31, 31), () => refresh.Remove(0));
        var full = Sample(() =>
        {
            for (var i = 0; i < 64; i++)
                refresh.Set(i, 31 + i * 83, (uint)(31 + i * 83));
        }, refresh.Reset);
        line($"Cache population, excluding CSS entity resolution: one changed slot {single.P50:F3}/{single.P99:F3} µs (p50/p99), 64 slots {full.P50:F3}/{full.P99:F3} µs.");
    }

    private static unsafe void ApplyBaseline(uint[] bits, int stride, int players, int[] indices, int[] owners)
    {
        if (indices.Length == 0) return;
        fixed (uint* data = bits)
        {
            for (var slot = 0; slot < players; slot++)
            {
                nint ptr = (nint)(data + slot * stride);
                var entities = Unsafe.As<nint, CFixedBitVecBase>(ref ptr);
                for (var i = 0; i < indices.Length; i++)
                    if (owners[i] != slot)
                        entities.Remove(indices[i]);
            }
        }
    }

    private static unsafe void ApplyCandidate(uint[] bits, int stride, int players, ObserverTargets targets)
    {
        if (targets.Count == 0) return;
        fixed (uint* data = bits)
        {
            for (var slot = 0; slot < players; slot++)
            {
                nint ptr = (nint)(data + slot * stride);
                targets.Hide(Unsafe.As<nint, CFixedBitVecBase>(ref ptr), slot);
            }
        }
    }

    private static (double P50, double P95, double P99, double Max, double Bytes) Sample(Action apply, Action reset)
    {
        var elapsed = new long[20_000];
        for (var i = 0; i < 10_000; i++)
        {
            reset();
            apply();
        }

        long bytes = 0;
        for (var i = 0; i < elapsed.Length; i++)
        {
            reset();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            apply();
            elapsed[i] = Stopwatch.GetTimestamp() - start;
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Array.Sort(elapsed);
        var toMicroseconds = 1_000_000d / Stopwatch.Frequency;
        double At(double percentile) => elapsed[(int)Math.Ceiling(elapsed.Length * percentile) - 1] * toMicroseconds;
        return (At(.5), At(.95), At(.99), At(1), (double)bytes / elapsed.Length);
    }
}
