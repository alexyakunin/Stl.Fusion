using System;
using System.Diagnostics.CodeAnalysis;
using System.Reactive;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stl.Async;
using Stl.Collections;
using Stl.Collections.Slim;
using Stl.Frozen;
using Stl.Fusion.Internal;
using Stl.Time;

namespace Stl.Fusion
{
    public enum ComputedState
    {
        Computing = 0,
        Consistent,
        Invalidated,
    }

    public interface IComputed : IResult
    {
        ComputedOptions Options { get; set; }
        ComputedInput Input { get; }
        IResult Output { get; }
        Type OutputType { get; }
        LTag LTag { get; } // ~ Unique for the specific (Func, Key) pair
        ComputedState State { get; }
        bool IsConsistent { get; }
        event Action<IComputed> Invalidated;
        Moment LastAccessTime { get; }

        bool Invalidate();
        ValueTask<IComputed> UpdateAsync(CancellationToken cancellationToken = default);
        ValueTask<IComputed> UpdateAsync(ComputeContext context, CancellationToken cancellationToken = default);

        void Touch();
        TResult Apply<TArg, TResult>(IComputedApplyHandler<TArg, TResult> handler, TArg arg);
    }
    
    public interface IComputed<TOut> : IComputed, IResult<TOut>
    {
        new Result<TOut> Output { get; }
        LTagged<Result<TOut>> LTaggedOutput { get; }
        bool TrySetOutput(Result<TOut> output);
        void SetOutput(Result<TOut> output);

        new ValueTask<IComputed<TOut>> UpdateAsync(CancellationToken cancellationToken = default);
        new ValueTask<IComputed<TOut>> UpdateAsync(ComputeContext context, CancellationToken cancellationToken = default);
    }
    
    public interface IComputedWithTypedInput<out TIn> : IComputed 
        where TIn : ComputedInput
    {
        new TIn Input { get; }
    }

    public interface IComputed<out TIn, TOut> : IComputed<TOut>, IComputedWithTypedInput<TIn> 
        where TIn : ComputedInput
    { }

    public class Computed<TIn, TOut> : IComputed<TIn, TOut>, IComputedImpl
        where TIn : ComputedInput
    {
        private ComputedOptions _options;
        private volatile int _state;
        private Result<TOut> _output;
        private RefHashSetSlim2<IComputedImpl> _used = default;
        private HashSetSlim2<(ComputedInput Input, LTag LTag)> _usedBy = default;
        // ReSharper disable once InconsistentNaming
        private event Action<IComputed>? _invalidated;
        private bool _invalidateOnSetOutput = false;
        private long _lastAccessTimeTicks;
        private object Lock => this;

        public ComputedOptions Options {
            get => _options;
            set {
                AssertStateIs(ComputedState.Computing);
                _options = value;
            }
        }

        public TIn Input { get; }
        public ComputedState State => (ComputedState) _state;
        public bool IsConsistent => State == ComputedState.Consistent;
        public IFunction<TIn, TOut> Function => (IFunction<TIn, TOut>) Input.Function; 
        public LTag LTag { get; }

        public Moment LastAccessTime {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new Moment(Volatile.Read(ref _lastAccessTimeTicks));
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected set => Volatile.Write(ref _lastAccessTimeTicks, value.EpochOffset.Ticks);
        }

        public Type OutputType => typeof(TOut);
        public Result<TOut> Output {
            get {
                AssertStateIsNot(ComputedState.Computing);
                return _output;
            }
        }
        public LTagged<Result<TOut>> LTaggedOutput => (Output, LTag);

        // IResult<T> properties
        public Exception? Error => Output.Error;
        public bool HasValue => Output.HasValue;
        public bool HasError => Output.HasError;
        public TOut UnsafeValue => Output.UnsafeValue;
        public TOut Value => Output.Value;

        // "Untyped" versions of properties
        ComputedInput IComputed.Input => Input;
        // ReSharper disable once HeapView.BoxingAllocation
        IResult IComputed.Output => Output;
        // ReSharper disable once HeapView.BoxingAllocation
        object? IResult.UnsafeValue => Output.UnsafeValue;
        // ReSharper disable once HeapView.BoxingAllocation
        object? IResult.Value => Output.Value;        

        public event Action<IComputed> Invalidated {
            add {
                if (State == ComputedState.Invalidated) {
                    value?.Invoke(this);
                    return;
                }
                lock (Lock) {
                    if (State == ComputedState.Invalidated) {
                        value?.Invoke(this);
                        return;
                    }
                    _invalidated += value;
                }
            }
            remove => _invalidated -= value;
        }

        public Computed(ComputedOptions options, TIn input, LTag lTag)
        {
            _options = options;
            Input = input;
            LTag = lTag;
            LastAccessTime = CoarseCpuClock.Now;
        }

        public Computed(ComputedOptions options, TIn input, Result<TOut> output, LTag lTag, bool isConsistent = true)
        {
            if (output.IsValue(out var v) && v is IFrozen f)
                f.Freeze();
            _options = options;
            Input = input;
            _state = (int) (isConsistent ? ComputedState.Consistent : ComputedState.Invalidated);
            _output = output;
            LTag = lTag;
            LastAccessTime = CoarseCpuClock.Now;
        }

        public override string ToString() 
            => $"{GetType().Name}({Input} {LTag}, State: {State})";

        void IComputedImpl.AddUsed(IComputedImpl used)
        {
            lock (Lock) {
                switch (State) {
                case ComputedState.Consistent:
                    throw Errors.WrongComputedState(State);
                case ComputedState.Invalidated:
                    return; // Already invalidated, so nothing to do here
                }
                used.AddUsedBy(this);
                _used.Add(used);
            }
        }

        void IComputedImpl.AddUsedBy(IComputedImpl usedBy)
        {
            lock (Lock) {
                switch (State) {
                case ComputedState.Computing:
                    throw Errors.WrongComputedState(State);
                case ComputedState.Invalidated:
                    usedBy.Invalidate();
                    return; 
                }
                _usedBy.Add((usedBy.Input, usedBy.LTag));
            }
        }

        void IComputedImpl.RemoveUsedBy(IComputedImpl usedBy)
        {
            lock (Lock) {
                _usedBy.Remove((usedBy.Input, usedBy.LTag));
            }
        }

        public bool TrySetOutput(Result<TOut> output)
        {
            if (output.IsValue(out var v) && v is IFrozen f)
                f.Freeze();
            if (State != ComputedState.Computing)
                return false;
            bool mustInvalidate;
            lock (Lock) {
                if (State != ComputedState.Computing)
                    return false;
                SetStateUnsafe(ComputedState.Consistent);
                _output = output;
                (mustInvalidate, _invalidateOnSetOutput) = (_invalidateOnSetOutput, false);
            }
            if (mustInvalidate)
                Invalidate();
            else {
                if (output.HasError)
                    AutoInvalidate(_options.ErrorAutoInvalidateTimeout);
                else if (_options.AutoInvalidateTimeout.HasValue)
                    AutoInvalidate(_options.AutoInvalidateTimeout.GetValueOrDefault());
            }
            return true;
        }

        public void SetOutput(Result<TOut> output)
        {
            if (!TrySetOutput(output))
                throw Errors.WrongComputedState(ComputedState.Computing, State);
        }

        public bool Invalidate()
        {
            if (State == ComputedState.Invalidated)
                return false;
            MemoryBuffer<(ComputedInput Input, LTag LTag)> usedBy = default;
            try {
                lock (Lock) {
                    switch (State) {
                    case ComputedState.Invalidated:
                        return false;
                    case ComputedState.Computing:
                        _invalidateOnSetOutput = true;
                        return true;
                    }
                    SetStateUnsafe(ComputedState.Invalidated);
                    usedBy = MemoryBuffer<(ComputedInput, LTag)>.LeaseAndSetCount(_usedBy.Count);
                    _usedBy.CopyTo(usedBy.Span);
                    _usedBy.Clear();
                    _used.Apply(this, (self, c) => c.RemoveUsedBy(self));
                    _used.Clear();
                }
                try {
                    _invalidated?.Invoke(this);
                }
                catch {
                    // We should never throw errors during the invalidation
                }
                for (var i = 0; i < usedBy.Span.Length; i++) {
                    ref var d = ref usedBy.Span[i];
                    d.Input.TryGetCachedComputed(d.LTag)?.Invalidate();
                    // Just in case buffers aren't cleaned up when you return them back
                    d = default!; 
                }
                return true;
            }
            finally {
                usedBy.Release();
            }
        }

        // UpdateAsync methods

        async ValueTask<IComputed> IComputed.UpdateAsync(CancellationToken cancellationToken) 
            => await UpdateAsync(null!, cancellationToken).ConfigureAwait(false);
        async ValueTask<IComputed> IComputed.UpdateAsync(ComputeContext context, CancellationToken cancellationToken) 
            => await UpdateAsync(cancellationToken).ConfigureAwait(false);
        public ValueTask<IComputed<TOut>> UpdateAsync(CancellationToken cancellationToken = default)
            => UpdateAsync(null!, cancellationToken);
        public async ValueTask<IComputed<TOut>> UpdateAsync(ComputeContext context, CancellationToken cancellationToken = default)
        {
            if (!IsConsistent)
                return await Function.InvokeAsync(Input, null, context, cancellationToken);

            using var contextUseScope = context.Use();
            context = contextUseScope.Context;

            if ((context.Options & ComputeOptions.Invalidate) == ComputeOptions.Invalidate)
                Invalidate();
            var usedBy = Computed.GetCurrent();
            ((IComputedImpl?) usedBy)?.AddUsed(this);
            context.TryCaptureValue(this);
            return this;
        }

        // Touch

        public void Touch() 
            => LastAccessTime = CoarseCpuClock.Now;

        // Apply

        public TResult Apply<TArg, TResult>(IComputedApplyHandler<TArg, TResult> handler, TArg arg) 
            => handler.Apply(this, arg);

        // IResult<T> methods

        public void Deconstruct(out TOut value, out Exception? error) 
            => Output.Deconstruct(out value, out error);
        public bool IsValue([MaybeNullWhen(false)] out TOut value)
            => Output.IsValue(out value);
        public bool IsValue([MaybeNullWhen(false)] out TOut value, [MaybeNullWhen(true)] out Exception error) 
            => Output.IsValue(out value, out error!);
        public void ThrowIfError() => Output.ThrowIfError();

        // Protected & private methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void SetStateUnsafe(ComputedState newState) 
            => _state = (int) newState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void AssertStateIs(ComputedState expectedState)
        {
            if (State != expectedState)
                throw Errors.WrongComputedState(expectedState, State);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void AssertStateIsNot(ComputedState unexpectedState)
        {
            if (State == unexpectedState)
                throw Errors.WrongComputedState(State);
        }

        private void AutoInvalidate(TimeSpan timeout)
        {
            // This method is called just once for sure
            var cts = new CancellationTokenSource(timeout);
            Invalidated += _ => {
                try {
                    if (!cts.IsCancellationRequested)
                        cts.Cancel(true);
                } catch {
                    // Intended: this method should never throw any exceptions
                }
            };
            cts.Token.Register(() => {
                cts.Dispose();
                // No need to schedule this via Task.Run, since this code is
                // either invoked from Invalidate method (via Invalidated handler),
                // so Invalidate() call will do nothing & return immediately,
                // or it's invoked via one of timer threads, i.e. where it's
                // totally fine to invoke Invalidate directly as well.
                Invalidate();
            }, false);
        }
    }
}
