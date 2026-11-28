using System.Collections.Concurrent;
using System.Diagnostics;

namespace MultiUserEdit.Commons
{
    internal sealed class TransferPacer(int maxWindow)
    {
        private const double TargetDelayMs = 100;
        private const int FineMinWindow = 2;
        private const int LegacyAckInterval = 4;
        private const int LegacyMinWindow = LegacyAckInterval + 1;
        private static readonly TimeSpan BaseDelayMemory = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<int, long> sentAt = new();
        private readonly Dictionary<Guid, double> queueDelays = [];
        private readonly HashSet<Guid> fineReceivers = [];
        private readonly Lock gate = new();
        private double window = LegacyMinWindow;
        private double baseDelayMs = double.MaxValue;
        private long baseDelayAt;
        private long lastDecreaseAt;
        private int lastAcked = -1;
        private bool slowStart = true;
        private volatile int minWindow = LegacyMinWindow;

        public int Window => Math.Clamp((int)Volatile.Read(ref window), minWindow, maxWindow);

        public void Sent(int chunkIndex) => sentAt[chunkIndex] = Stopwatch.GetTimestamp();

        public void Acked(Guid receiver, int chunkIndex)
        {
            if (!sentAt.TryGetValue(chunkIndex, out var sent)) return;

            var now = Stopwatch.GetTimestamp();
            var delayMs = Stopwatch.GetElapsedTime(sent, now).TotalMilliseconds;

            lock (gate)
            {
                if (chunkIndex % LegacyAckInterval != 0) fineReceivers.Add(receiver);
                minWindow = queueDelays.Keys.Append(receiver).All(fineReceivers.Contains) ? FineMinWindow : LegacyMinWindow;

                if (delayMs < baseDelayMs || Stopwatch.GetElapsedTime(baseDelayAt, now) > BaseDelayMemory)
                {
                    baseDelayMs = delayMs;
                    baseDelayAt = now;
                }

                queueDelays[receiver] = delayMs - baseDelayMs;
                var queued = queueDelays.Values.Max();
                var newlyAcked = Math.Max(1, chunkIndex - lastAcked);
                lastAcked = Math.Max(lastAcked, chunkIndex);

                if (queued > TargetDelayMs) slowStart = false;

                if (slowStart)
                {
                    window = Math.Min(maxWindow, window + newlyAcked);
                }
                else if (queued > TargetDelayMs * 2)
                {
                    if (Stopwatch.GetElapsedTime(lastDecreaseAt, now).TotalMilliseconds > delayMs)
                    {
                        window = Math.Max(minWindow, window / 2);
                        lastDecreaseAt = now;
                    }
                }
                else
                {
                    window = Math.Clamp(window + (TargetDelayMs - queued) / TargetDelayMs * newlyAcked / window, minWindow, maxWindow);
                }
            }

            foreach (var index in sentAt.Keys)
            {
                if (index <= chunkIndex - maxWindow) sentAt.TryRemove(index, out _);
            }
        }
    }
}
