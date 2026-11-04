// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using ProxyPreferences = XMAT.WebServiceCapture.Models.PreferencesModel;

namespace XMAT.ApplicationServices
{
    internal sealed class ApplicationPreferences : IApplicationPreferences
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly ProxyPortPool _ports;
        private readonly Func<Task> _save;
        private readonly Action<string> _applyTheme;
        private readonly Func<IReadOnlyList<string>> _languages;
        private readonly object _drainGate = new();
        private TaskCompletionSource _drained;
        private int _pendingChanges;
        private AppPreferencesModel _observed;
        private long _observedRevision;
        private long _revision;
        private bool _changing;

        internal ApplicationPreferences(
            CaptureAppModel model, Dispatcher dispatcher, ProxyPortPool ports = null,
            Func<Task> save = null, Action<string> applyTheme = null, Func<IReadOnlyList<string>> languages = null)
        {
            _model = model;
            _dispatcher = dispatcher;
            _ports = ports ?? ProxyPortPool.Shared;
            _save = save ?? (() => CaptureAppSettings.SerializeAsync(model));
            _applyTheme = applyTheme ?? ThemeManager.ApplyTheme;
            _languages = languages ?? InstalledLanguages;
        }

        public async Task<ApplicationPreferenceSnapshot> InspectAsync(CancellationToken cancellationToken = default)
        {
            var options = await OptionsAsync(cancellationToken).ConfigureAwait(false);
            return await InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preferences = Synchronize();
                return Task.FromResult(new ApplicationPreferenceSnapshot(
                    _revision, ReadValues(preferences), options, _ports.GetReservedPorts(), _changing));
            }).ConfigureAwait(false);
        }

        public async Task<ApplicationPreferencePlan> PrepareAsync(
            long expectedRevision, ApplicationPreferenceValues values, CancellationToken cancellationToken = default)
        {
            var options = await OptionsAsync(cancellationToken).ConfigureAwait(false);
            return await InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Prepare(expectedRevision, values, options));
            }).ConfigureAwait(false);
        }

        public Task<ApplicationPreferenceChange> ApplyAsync(
            long expectedRevision, ApplicationPreferenceValues values, CancellationToken cancellationToken = default) =>
            TrackChangeAsync(async () =>
            {
                var options = await OptionsAsync(cancellationToken).ConfigureAwait(false);
                var plan = await InvokeAsync(() => Task.FromResult(Prepare(expectedRevision, values, options))).ConfigureAwait(false);
                return await ExecuteCoreAsync(plan, options, cancellationToken).ConfigureAwait(false);
            });

        public Task<ApplicationPreferenceChange> ExecuteAsync(
            ApplicationPreferencePlan plan, CancellationToken cancellationToken = default) =>
            TrackChangeAsync(async () =>
            {
                var options = await OptionsAsync(cancellationToken).ConfigureAwait(false);
                return await ExecuteCoreAsync(plan, options, cancellationToken).ConfigureAwait(false);
            });

        private async Task<ApplicationPreferenceChange> TrackChangeAsync(Func<Task<ApplicationPreferenceChange>> action)
        {
            lock (_drainGate)
            {
                if (_pendingChanges++ == 0)
                    _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                lock (_drainGate)
                {
                    if (--_pendingChanges == 0)
                        _drained.TrySetResult();
                }
            }
        }

        public Task DrainAsync()
        {
            lock (_drainGate)
            { return _pendingChanges == 0 ? Task.CompletedTask : _drained.Task; }
        }

        private Task<ApplicationPreferenceChange> ExecuteCoreAsync(
            ApplicationPreferencePlan plan, ApplicationPreferenceOptions options, CancellationToken cancellationToken) =>
            InvokeAsync<ApplicationPreferenceChange>(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan == null)
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                var current = Prepare(plan.ExpectedRevision, plan.After, options);
                if (current.Before != plan.Before || current.After != plan.After ||
                    current.LanguageRestartRequired != plan.LanguageRestartRequired ||
                    !current.Differences.SequenceEqual(plan.Differences))
                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                _changing = true;
                try
                {
                    var committedRevision = _revision;
                    if (current.Differences.Count > 0)
                    {
                        if (current.Before.Theme != current.After.Theme)
                        {
                            try
                            {
                                _applyTheme(current.After.Theme);
                                Synchronize();
                                if (_revision != current.ExpectedRevision)
                                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                            }
                            catch
                            {
                                _applyTheme(current.Before.Theme);
                                throw;
                            }
                        }
                        // Pool reconfiguration never releases or moves an existing reservation.
                        _ports.Initialize((ushort)current.After.FirstProxyPort, (ushort)current.After.LastProxyPort);
                        ApplyValues(_observed, current.After, modelRevision =>
                        {
                            _revision += modelRevision - _observedRevision;
                            _observedRevision = modelRevision;
                            committedRevision = _revision;
                        });
                    }
                    try
                    {
                        // Once committed, finish the durable snapshot even if the caller disconnects.
                        await _save();
                        return new(committedRevision, current.Differences.Count > 0, true, true,
                            current.LanguageRestartRequired);
                    }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                    {
                        return new(committedRevision, current.Differences.Count > 0, true, false,
                            current.LanguageRestartRequired, AdministrationFailure.PersistenceFailed);
                    }
                }
                finally { _changing = false; }
            });

        private ApplicationPreferencePlan Prepare(
            long expectedRevision, ApplicationPreferenceValues values, ApplicationPreferenceOptions options)
        {
            var preferences = Synchronize();
            if (_changing)
                throw new AdministrationException(AdministrationFailure.Unavailable);
            if (_revision != expectedRevision)
                throw new AdministrationException(AdministrationFailure.SourceChanged);
            Validate(values, options);
            var normalized = values with
            {
                Language = options.Languages.Single(language => language.Equals(values.Language, StringComparison.OrdinalIgnoreCase))
            };
            var before = ReadValues(preferences);
            var differences = Differences(before, normalized);
            return new(_revision, before, normalized, differences, before.Language != normalized.Language);
        }

        private AppPreferencesModel Synchronize()
        {
            var current = _model.PreferencesModel;
            if (current?.WebProxyCapturePreferences is not ProxyPreferences ||
                current.NetworkAnalysisPreferences is not NetworkTraceAnalysisOptionsModel)
                throw new AdministrationException(AdministrationFailure.PrerequisiteRequired);
            if (!ReferenceEquals(current, _observed))
            {
                _observed = current;
                _observedRevision = current.Revision;
                _revision++;
            }
            else if (current.Revision != _observedRevision)
            {
                _revision += Math.Max(1, current.Revision - _observedRevision);
                _observedRevision = current.Revision;
            }
            return current;
        }

        private Task<ApplicationPreferenceOptions> OptionsAsync(CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ApplicationPreferenceOptions(
                    _languages().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(64).ToArray(),
                    ["Light", "Dark"]);
            }, cancellationToken);

        internal static ApplicationPreferenceValues ReadValues(AppPreferencesModel preferences)
        {
            if (preferences?.WebProxyCapturePreferences is not ProxyPreferences proxy ||
                preferences.NetworkAnalysisPreferences is not NetworkTraceAnalysisOptionsModel analysis)
                throw new AdministrationException(AdministrationFailure.PrerequisiteRequired);
            return new(proxy.FirstPort, proxy.LastPort, proxy.PromptToDisableOnClose,
                analysis.PacketsPerSecondThreshold, analysis.MaximumMTU, analysis.DuplicatePacketWindow,
                preferences.Language, preferences.Theme);
        }

        internal static AppPreferencesModel CreateDraft(ApplicationPreferenceValues values)
        {
            var draft = new AppPreferencesModel
            {
                WebProxyCapturePreferences = new ProxyPreferences(),
                NetworkAnalysisPreferences = new NetworkTraceAnalysisOptionsModel()
            };
            ApplyValues(draft, values);
            return draft;
        }

        private static long ApplyValues(
            AppPreferencesModel preferences, ApplicationPreferenceValues values, Action<long> beforeNotifications = null)
        {
            var proxy = (ProxyPreferences)preferences.WebProxyCapturePreferences;
            var analysis = (NetworkTraceAnalysisOptionsModel)preferences.NetworkAnalysisPreferences;
            using var batch = preferences.BatchChanges();
            using var proxyBatch = proxy.BatchChanges();
            using var analysisBatch = analysis.BatchChanges();
            proxy.FirstPort = (ushort)values.FirstProxyPort;
            proxy.LastPort = (ushort)values.LastProxyPort;
            proxy.PromptToDisableOnClose = values.PromptToDisableProxyOnClose;
            analysis.PacketsPerSecondThreshold = values.PacketsPerSecondThreshold;
            analysis.MaximumMTU = values.MaximumMtu;
            analysis.DuplicatePacketWindow = values.DuplicatePacketWindowMilliseconds;
            preferences.Language = values.Language;
            preferences.Theme = values.Theme;
            beforeNotifications?.Invoke(preferences.Revision);
            return preferences.Revision;
        }

        private static void Validate(ApplicationPreferenceValues values, ApplicationPreferenceOptions options)
        {
            if (values == null ||
                values.FirstProxyPort is < 1 or > 65535 ||
                values.LastProxyPort < values.FirstProxyPort || values.LastProxyPort > 65535 ||
                values.PacketsPerSecondThreshold < 0 || values.MaximumMtu is < 1 or > 65535 ||
                values.DuplicatePacketWindowMilliseconds < 0 ||
                !options.Languages.Contains(values.Language, StringComparer.OrdinalIgnoreCase) ||
                !options.Themes.Contains(values.Theme, StringComparer.Ordinal))
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
        }

        private static IReadOnlyList<ApplicationPreferenceDifference> Differences(
            ApplicationPreferenceValues before, ApplicationPreferenceValues after)
        {
            var differences = new List<ApplicationPreferenceDifference>();
            Add(nameof(after.FirstProxyPort), before.FirstProxyPort, after.FirstProxyPort);
            Add(nameof(after.LastProxyPort), before.LastProxyPort, after.LastProxyPort);
            Add(nameof(after.PromptToDisableProxyOnClose), before.PromptToDisableProxyOnClose, after.PromptToDisableProxyOnClose);
            Add(nameof(after.PacketsPerSecondThreshold), before.PacketsPerSecondThreshold, after.PacketsPerSecondThreshold);
            Add(nameof(after.MaximumMtu), before.MaximumMtu, after.MaximumMtu);
            Add(nameof(after.DuplicatePacketWindowMilliseconds), before.DuplicatePacketWindowMilliseconds, after.DuplicatePacketWindowMilliseconds);
            Add(nameof(after.Language), before.Language, after.Language);
            Add(nameof(after.Theme), before.Theme, after.Theme);
            return differences.ToArray();

            void Add<T>(string field, T oldValue, T newValue)
            {
                if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
                    differences.Add(new(field, Convert.ToString(oldValue, CultureInfo.InvariantCulture),
                        Convert.ToString(newValue, CultureInfo.InvariantCulture)));
            }
        }

        private static IReadOnlyList<string> InstalledLanguages()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Langs");
            if (!Directory.Exists(directory))
                return [];
            return Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
                .Where(name => name.Length is > 0 and <= 32 &&
                    name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')).Take(64).ToArray();
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) =>
            App.RunOnUiAsync(_dispatcher, action);
    }
}
