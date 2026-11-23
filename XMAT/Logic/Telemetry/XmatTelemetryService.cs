// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;

namespace XMAT.Telemetry
{
    internal sealed class XmatTelemetryService : IDisposable
    {
        private readonly ITelemetryReporter _reporter;
        private readonly Action<string> _diagnose;
        private readonly long[] _uses = new long[Enum.GetValues<TelemetryFeature>().Length];
        private readonly long[] _failures = new long[Enum.GetValues<TelemetryFeature>().Length];
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        private int _closing;
        private int _ready;
        private readonly ConditionalWeakTable<Exception, object> _unhandled = new();
        private int _peakCaptures;
        private int _activeCaptures;
        private readonly HashSet<TelemetryOperation> _operations = [];
        private readonly object _operationGate = new();
        private readonly object _sendGate = new();
        private static bool _sampling;
        private static Action _liveFailure;
        private static readonly LiveTelemetryReporterOwner LiveReporter = new(environment =>
        {
            var payload = new XboxTelemetryPayload(environment);
            return XboxTelemetryReporter.Create(XboxSdkClient.Create, payload.Serialize);
        });
        private static readonly Lazy<TelemetryConsentPreferences> ConsentPreferences = new(() => new(
            TelemetryConsentStore.ForCurrentUser(Diagnose), StopConsentedSession, StartConsentedSession));

        internal static XmatTelemetryService Current { get; private set; } = new(null, Diagnose);
        internal static TelemetryConsentPreferences Consent => ConsentPreferences.Value;
        internal static bool IsSampling => _sampling;
        internal bool IsEnabled => _reporter != null && Volatile.Read(ref _disposed) == 0;
        internal Guid SessionId { get; } = Guid.NewGuid();

        internal XmatTelemetryService(ITelemetryReporter reporter, Action<string> diagnose)
        {
            _reporter = reporter;
            _diagnose = diagnose;
        }

        internal static void Initialize(string[] args = null, Action sampleFailure = null, Action liveFailure = null)
        {
            args ??= [];
            _liveFailure = liveFailure;
            var sampleRequested = Array.IndexOf(args, "--telemetry-sample") >= 0;
            _sampling = sampleRequested;
            try
            {
                var configuration = TelemetryConfiguration.FromAssembly();
                var samplePath = LocalTelemetrySampleReporter.SamplePath(args);
                Current = samplePath == null
                    ? CreateLive(configuration)
                    : CreateSample(samplePath,
                        channel => TelemetryEnvironmentProvider.Read(channel, Diagnose), Diagnose);
                Current.Track(new(TelemetryEventKind.AppLaunched));
            }
            catch (Exception)
            {
                Diagnose("InitializationFailed");
                if (sampleRequested)
                {
                    sampleFailure?.Invoke();
                }
                else
                {
                    liveFailure?.Invoke();
                }
            }
        }

        private static XmatTelemetryService CreateLive(TelemetryConfiguration configuration)
        {
            var consent = Consent.State;
            var service = Create(configuration, channel => TelemetryEnvironmentProvider.Read(channel, Diagnose),
                (_, environment) => LiveReporter.GetOrCreate(environment), Diagnose, consent);
            if (consent == TelemetryConsentState.Accepted && !IsSuppressed && !service.IsEnabled)
            {
                _liveFailure?.Invoke();
            }
            return service;
        }

        private static void StopConsentedSession()
        {
            if (!_sampling)
            {
                Current.Revoke();
            }
        }

        internal static void StartConsentedSession()
        {
            if (!_sampling)
            {
                try
                {
                    Current = CreateLive(TelemetryConfiguration.FromAssembly());
                    Current.Track(new(TelemetryEventKind.AppLaunched));
                }
                catch (Exception)
                {
                    Diagnose("InitializationFailed");
                    _liveFailure?.Invoke();
                }
            }
        }

        internal static XmatTelemetryService CreateSample(string samplePath,
            Func<TelemetryBuildChannel, IReadOnlyList<KeyValuePair<string, object>>> environment,
            Action<string> diagnose)
        {
            if (IsSuppressed)
            {
                diagnose("LocalSampleSuppressed");
                return new(null, diagnose);
            }
            return new(new LocalTelemetrySampleReporter(samplePath, environment(TelemetryBuildChannel.Local), diagnose), diagnose);
        }

        internal static XmatTelemetryService Create(
            TelemetryConfiguration configuration,
            Func<TelemetryBuildChannel, IReadOnlyList<KeyValuePair<string, object>>> environment,
            Func<TelemetryConfiguration, IReadOnlyList<KeyValuePair<string, object>>, ITelemetryReporter> reporter,
            Action<string> diagnose, TelemetryConsentState consent = TelemetryConsentState.Missing)
        {
            if (consent != TelemetryConsentState.Accepted || IsSuppressed)
            {
                return new(null, diagnose);
            }
            try
            {
                return new(reporter(configuration, environment(configuration.Channel)), diagnose);
            }
            catch (Exception)
            {
                diagnose("InitializationFailed");
                return new(null, diagnose);
            }
        }

        internal static bool IsSuppressed =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XMAT_SUPPRESS_TELEMETRY"));

        internal static void Shutdown()
        {
            try
            {
                Current.Dispose();
            }
            finally
            {
                try
                {
                    LiveReporter.Dispose();
                }
                catch (Exception)
                {
                    Diagnose("ShutdownFailed");
                }
            }
        }

        internal void Track(TelemetryEvent item)
        {
            lock (_sendGate)
            {
                if (!IsEnabled)
                {
                    return;
                }
                if (!item.IsValid)
                {
                    _diagnose("InvalidEvent");
                    return;
                }
                try
                {
                    _reporter.Send(item, SessionId);
                }
                catch (Exception)
                {
                    _diagnose("SubmissionFailed");
                }
            }
        }

        internal void Revoke()
        {
            lock (_sendGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }
                try
                {
                    _reporter?.Revoke();
                }
                catch (Exception)
                {
                    _diagnose("RevocationFailed");
                }
                Array.Clear(_uses);
                Array.Clear(_failures);
            }
            lock (_operationGate)
            {
                _operations.Clear();
            }
        }

        internal void Use(TelemetryFeature feature)
        {
            if (!IsEnabled)
            {
                return;
            }
            if (!Enum.IsDefined(feature))
            {
                _diagnose("InvalidFeature");
                return;
            }
            if (feature != TelemetryFeature.None && Interlocked.Increment(ref _uses[(int)feature]) == 1)
            {
                Track(new(TelemetryEventKind.FeatureUsed, feature));
            }
        }

        internal void ScriptFailure(TelemetryFeature feature)
        {
            if (IsEnabled && Enum.IsDefined(feature))
            {
                Interlocked.Increment(ref _failures[(int)feature]);
            }
        }

        internal void Interact(TelemetryFeature feature, TelemetryInteraction interaction)
        {
            Use(feature);
            Track(new(TelemetryEventKind.Counter, feature, Count: 1, Interaction: interaction));
        }

        internal TelemetryOperation Begin(TelemetryOperationKind operation, TelemetryFeature feature = TelemetryFeature.None,
            TelemetryContext? context = null, TelemetryEntryPoint entryPoint = TelemetryEntryPoint.None)
        {
            lock (_operationGate)
            {
                var result = new TelemetryOperation(this, operation, feature, context, entryPoint);
                if (IsEnabled && Volatile.Read(ref _closing) == 0)
                {
                    _operations.Add(result);
                    result.Start();
                }
                return result;
            }
        }

        internal void OperationEnded(TelemetryOperation operation)
        {
            lock (_operationGate)
            {
                _operations.Remove(operation);
            }
        }

        internal void Ready()
        {
            if (Interlocked.Exchange(ref _ready, 1) == 0)
            {
                Track(new(TelemetryEventKind.AppReady, DurationMs: ElapsedMilliseconds));
            }
        }

        internal void UnhandledFailure(Exception exception)
        {
            if (!IsEnabled || exception == null)
            {
                return;
            }
            lock (_unhandled)
            {
                if (_unhandled.TryGetValue(exception, out _))
                {
                    return;
                }
                _unhandled.Add(exception, new());
            }
            Track(new(TelemetryEventKind.UnhandledFailure, Error: TelemetryBuckets.Error(exception)));
        }

        internal long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        internal void CaptureStarted()
        {
            if (IsEnabled)
            {
                var active = Interlocked.Increment(ref _activeCaptures);
                int peak;
                do
                {
                    peak = Volatile.Read(ref _peakCaptures);
                } while (active > peak && Interlocked.CompareExchange(ref _peakCaptures, active, peak) != peak);
            }
        }

        internal void CaptureEnded()
        {
            if (IsEnabled)
            {
                Interlocked.Decrement(ref _activeCaptures);
            }
        }

        internal static TelemetryContext? ContextFor(IDeviceCaptureController controller)
        {
            if (!Current.IsEnabled)
            {
                return null;
            }
            var context = CaptureAppModel.AppModel.CaptureDeviceContexts.FirstOrDefault(x => x.CaptureController == controller);
            return context == null ? null : ContextFor(context);
        }

        internal static TelemetryContext ContextFor(ICaptureDeviceContext context)
        {
            return new(context.CaptureType, context.DeviceType,
                context is CaptureDeviceContextModel model ? model.Origin : CaptureOrigin.None);
        }

        public void Dispose()
        {
            if (!IsEnabled || Interlocked.Exchange(ref _closing, 1) != 0)
            {
                return;
            }
            TelemetryOperation[] pending;
            lock (_operationGate)
            {
                pending = _operations.ToArray();
            }
            foreach (var operation in pending)
            {
                operation.Complete(TelemetryOutcome.Cancelled, TelemetryError.Cancelled);
            }
            foreach (var feature in Enum.GetValues<TelemetryFeature>())
            {
                var count = Volatile.Read(ref _uses[(int)feature]);
                if (count > 0)
                {
                    Track(new(TelemetryEventKind.Counter, feature, Count: count));
                }
                var failures = Volatile.Read(ref _failures[(int)feature]);
                if (failures > 0)
                {
                    Track(new(TelemetryEventKind.Counter, feature, Outcome: TelemetryOutcome.Failed, Error: TelemetryError.Unknown, Count: failures));
                }
            }
            Track(new(TelemetryEventKind.SessionSummary, DurationMs: ElapsedMilliseconds, Count: Volatile.Read(ref _peakCaptures)));
            Track(new(TelemetryEventKind.AppClosed, DurationMs: ElapsedMilliseconds));
            lock (_sendGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try
                    {
                        _reporter.Dispose();
                    }
                    catch (Exception)
                    {
                        _diagnose("ShutdownFailed");
                    }
                }
            }
        }

        private static void Diagnose(string code) => Trace.TraceWarning("XMAT telemetry: {0}", code);
    }

    internal sealed class TelemetryOperation : IDisposable
    {
        private readonly XmatTelemetryService _service;
        private readonly TelemetryEvent _template;
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _stageStarted;
        private TelemetryStage _stage;
        private int _finished;
        private bool _active;
        private long _items;
        private readonly object _gate = new();

        internal TelemetryOperation(XmatTelemetryService service, TelemetryOperationKind kind,
            TelemetryFeature feature, TelemetryContext? context, TelemetryEntryPoint entryPoint)
        {
            _service = service;
            _template = new(TelemetryEventKind.OperationStarted, feature, kind, Context: context,
                OperationId: Guid.NewGuid(), EntryPoint: entryPoint);
        }

        internal void Start()
        {
            _active = true;
            _service.Use(_template.Feature);
            _service.Track(_template);
            if (_template.Operation == TelemetryOperationKind.Capture)
            {
                _service.CaptureStarted();
            }
        }

        internal void ItemReceived()
        {
            if (!_service.IsEnabled)
            {
                return;
            }
            lock (_gate)
            {
                if (_active && _finished == 0 && ++_items == 1)
                {
                    _service.Track(_template with { Kind = TelemetryEventKind.CaptureFirstData, DurationMs = ElapsedMilliseconds });
                }
            }
        }

        internal void MarkStage(TelemetryStage stage)
        {
            lock (_gate)
            {
                if (!_active || _finished != 0)
                {
                    return;
                }
                FlushStage();
                _stage = stage;
                _stageStarted = Stopwatch.GetTimestamp();
            }
        }

        private void FlushStage()
        {
            if (_stage != TelemetryStage.None)
            {
                _service.Track(_template with
                {
                    Kind = TelemetryEventKind.OperationStage,
                    Stage = _stage,
                    DurationMs = (long)Stopwatch.GetElapsedTime(_stageStarted).TotalMilliseconds
                });
            }
            _stage = TelemetryStage.None;
        }

        internal void Complete(TelemetryOutcome outcome = TelemetryOutcome.Succeeded, TelemetryError error = TelemetryError.None,
            long items = -1, long fileSize = -1, TelemetryContext? context = null)
        {
            lock (_gate)
            {
                if (!_active || _finished != 0)
                {
                    return;
                }
                _finished = 1;
                FlushStage();
                var count = items < 0 ? _items : items;
                if (_template.Operation == TelemetryOperationKind.Capture && outcome == TelemetryOutcome.Succeeded && count == 0)
                {
                    outcome = TelemetryOutcome.NoData;
                }
                _service.Track(_template with
                {
                    Kind = TelemetryEventKind.OperationCompleted,
                    Outcome = outcome,
                    Error = error,
                    DurationMs = ElapsedMilliseconds,
                    ItemCount = TelemetryBuckets.Items(count),
                    FileSize = TelemetryBuckets.Size(fileSize),
                    Context = context ?? _template.Context
                });
                if (_template.Operation == TelemetryOperationKind.Capture)
                {
                    _service.CaptureEnded();
                }
            }
            _service.OperationEnded(this);
        }

        internal void Fail(Exception exception) => Complete(
            exception is OperationCanceledException ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed,
            TelemetryBuckets.Error(exception));

        internal void Complete(OperationOutcome outcome, long items = -1, long fileSize = -1)
        {
            var (result, error) = outcome switch
            {
                OperationOutcome.Succeeded => (TelemetryOutcome.Succeeded, TelemetryError.None),
                OperationOutcome.Cancelled => (TelemetryOutcome.Cancelled, TelemetryError.Cancelled),
                OperationOutcome.NoAnalyzableData => (TelemetryOutcome.NoData, TelemetryError.None),
                OperationOutcome.InvalidInput => (TelemetryOutcome.Failed, TelemetryError.InvalidData),
                OperationOutcome.Conflict => (TelemetryOutcome.Failed, TelemetryError.InvalidOperation),
                OperationOutcome.PrerequisiteMissing => (TelemetryOutcome.Failed, TelemetryError.Configuration),
                _ => (TelemetryOutcome.Failed, TelemetryError.Unknown)
            };
            Complete(result, error, items, fileSize);
        }

        internal void CompleteReplay(OperationResult<ReplayResult> result, long sends)
        {
            var failed = result.Succeeded
                ? result.Value?.Items.FirstOrDefault(item => item.Outcome != ReplayOutcome.Completed)
                : null;
            if (failed != null)
            {
                Complete(TelemetryOutcome.Failed,
                    failed.Outcome == ReplayOutcome.TooLarge ? TelemetryError.InvalidData : TelemetryError.Network,
                    items: sends);
            }
            else
            {
                Complete(result.Outcome, items: sends);
            }
        }

        private long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        public void Dispose() => Complete(TelemetryOutcome.Failed, TelemetryError.Unknown);
    }
}
