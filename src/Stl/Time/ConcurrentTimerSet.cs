using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Stl.Async;
using Stl.Mathematics;
using Stl.OS;

namespace Stl.Time
{
    public sealed class ConcurrentTimerSet<TTimer> : AsyncDisposableBase
        where TTimer : notnull
    {
        public record Options : TimerSet<TTimer>.Options
        {
            public int ConcurrencyLevel { get; init; } = HardwareInfo.GetProcessorCountPo2Factor(5);
        }

        private readonly TimerSet<TTimer>[] _timerSets;
        private readonly int _concurrencyLevelMask;

        public TimeSpan Quanta { get; }
        public IMomentClock Clock { get; }
        public int ConcurrencyLevel { get; }
        public int Count => _timerSets.Sum(ts => ts.Count);

        public ConcurrentTimerSet(Options? options = null, Action<TTimer>? fireHandler = null)
        {
            options ??= new();
            Quanta = options.Quanta;
            Clock = options.Clock;
            ConcurrencyLevel = (int) Bits.GreaterOrEqualPowerOf2((ulong) Math.Max(1, options.ConcurrencyLevel));
            _concurrencyLevelMask = ConcurrencyLevel - 1;
            _timerSets = new TimerSet<TTimer>[ConcurrencyLevel];
            for (var i = 0; i < _timerSets.Length; i++)
                _timerSets[i] = new TimerSet<TTimer>(options, fireHandler);
        }

        protected override async ValueTask DisposeInternal(bool disposing)
        {
            var tasks = new List<Task>(_timerSets.Length);
            foreach (var timerSet in _timerSets)
                tasks.Add(timerSet.DisposeAsync().AsTask());
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void AddOrUpdate(TTimer timer, Moment time)
            => GetTimerSet(timer).AddOrUpdate(timer, time);
        public bool AddOrUpdateToEarlier(TTimer timer, Moment time)
            => GetTimerSet(timer).AddOrUpdateToEarlier(timer, time);
        public bool AddOrUpdateToLater(TTimer timer, Moment time)
            => GetTimerSet(timer).AddOrUpdateToLater(timer, time);
        public bool Remove(TTimer timer)
            => GetTimerSet(timer).Remove(timer);

        private TimerSet<TTimer> GetTimerSet(TTimer timer)
        {
            var hashCode = timer.GetHashCode();
            return _timerSets[hashCode & _concurrencyLevelMask];
        }
    }
}
