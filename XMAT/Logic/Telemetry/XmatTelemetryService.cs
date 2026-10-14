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

        internal static XmatTelemetryService Current { get; private set; } = new(null, Diagnose);
        internal bool IsEnabled => _reporter != null && Volatile.Read(ref _disposed) == 0;
        internal Guid SessionId { get; } = Guid.NewGuid();

        internal XmatTelemetryService(ITelemetryReporter reporter, Action<string> diagnose)
        {
            _reporter = reporter;
            _diagnose = diagnose;
        }

        internal static void Initialize(string[] args = null)
        {
            args ??= [];
            var sampleRequested = Array.IndexOf(args, "--telemetry-sample") >= 0;
            try
            {
                var configuration = TelemetryConfiguration.FromAssembly();
                var samplePath = LocalTelemetrySampleReporter.SamplePath(args);
                Current = samplePath == null
                    ? Create(configuration,
                        channel => TelemetryEnvironmentProvider.Read(channel, Diagnose),
                        (options, fields) => new OneCollectorTelemetryReporter(options, fields, Diagnose), Diagnose)
                    : CreateSample(configuration, samplePath,
                        channel => TelemetryEnvironmentProvider.Read(channel, Diagnose), Diagnose);
                Current.Track(new(TelemetryEventKind.AppLaunched));
            }
            catch (Exception)
            {
                Diagnose("InitializationFailed");
                if (sampleRequested)
                {
                    System.Windows.MessageBox.Show(
                        Localization.GetLocalizedString("TELEMETRY_SAMPLE_FAILED_MESSAGE"),
                        Localization.GetLocalizedString("TELEMETRY_SAMPLE_FAILED_TITLE"),
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }

        internal static XmatTelemetryService CreateSample(TelemetryConfiguration configuration, string samplePath,
            Func<TelemetryBuildChannel, IReadOnlyList<KeyValuePair<string, object>>> environment,
            Action<string> diagnose)
        {
            if (configuration.Enabled)
            {
                throw new InvalidOperationException("Local sampling requires a non-exporting build.");
            }
            return new(new LocalTelemetrySampleReporter(samplePath, environment(TelemetryBuildChannel.Local), diagnose), diagnose);
        }

        internal static XmatTelemetryService Create(
            TelemetryConfiguration configuration,
            Func<TelemetryBuildChannel, IReadOnlyList<KeyValuePair<string, object>>> environment,
            Func<TelemetryConfiguration, IReadOnlyList<KeyValuePair<string, object>>, ITelemetryReporter> reporter,
            Action<string> diagnose)
        {
            if (!configuration.IsValid)
            {
                diagnose("InvalidConfiguration");
                return new(null, diagnose);
            }
            if (!configuration.Enabled)
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

        internal void Track(TelemetryEvent item)
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
            return new(this, operation, feature, context, entryPoint);
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
        private long _items;
        private readonly object _gate = new();

        internal TelemetryOperation(XmatTelemetryService service, TelemetryOperationKind kind,
            TelemetryFeature feature, TelemetryContext? context, TelemetryEntryPoint entryPoint)
        {
            _service = service;
            _template = new(TelemetryEventKind.OperationStarted, feature, kind, Context: context,
                OperationId: Guid.NewGuid(), EntryPoint: entryPoint);
            service.Use(feature);
            service.Track(_template);
            if (kind == TelemetryOperationKind.Capture)
            {
                service.CaptureStarted();
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
                if (_finished == 0 && ++_items == 1)
                {
                    _service.Track(_template with { Kind = TelemetryEventKind.CaptureFirstData, DurationMs = ElapsedMilliseconds });
                }
            }
        }

        internal void MarkStage(TelemetryStage stage)
        {
            lock (_gate)
            {
                if (_finished != 0)
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
                if (_finished != 0)
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
        }

        internal void Fail(Exception exception) => Complete(
            exception is OperationCanceledException ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed,
            TelemetryBuckets.Error(exception));

        private long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        public void Dispose() => Complete(TelemetryOutcome.Failed, TelemetryError.Unknown);
    }
}
