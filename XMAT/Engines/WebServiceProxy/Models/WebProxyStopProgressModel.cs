// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace XMAT.WebServiceCapture.Models
{
    internal sealed class WebProxyStopProgressModel : INotifyPropertyChanged
    {
        public string PhaseText { get; private set; } =
            Localization.GetLocalizedString("WEBCAP_STOP_PHASE_PUBLIC");
        public string ElapsedText { get; private set; } =
            Localization.GetLocalizedString("WEBCAP_STOP_ELAPSED", "00:00");
        public string ActiveCallbacksText { get; private set; } =
            Localization.GetLocalizedString("WEBCAP_STOP_ACTIVE_CALLBACKS", 0);
        public string PendingUiUpdatesText { get; private set; } =
            Localization.GetLocalizedString("WEBCAP_STOP_PENDING_UI", 0);

        internal void Update(WebProxyStopProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);

            PhaseText = Localization.GetLocalizedString(
                ProgressPhaseLocalizationKey(progress.Phase));
            ElapsedText = Localization.GetLocalizedString(
                "WEBCAP_STOP_ELAPSED",
                progress.Elapsed.ToString(@"mm\:ss"));
            ActiveCallbacksText = Localization.GetLocalizedString(
                "WEBCAP_STOP_ACTIVE_CALLBACKS",
                progress.ActiveCallbacks);
            PendingUiUpdatesText = Localization.GetLocalizedString(
                "WEBCAP_STOP_PENDING_UI",
                progress.PendingUiUpdates);

            RaisePropertyChange(nameof(PhaseText));
            RaisePropertyChange(nameof(ElapsedText));
            RaisePropertyChange(nameof(ActiveCallbacksText));
            RaisePropertyChange(nameof(PendingUiUpdatesText));
        }

        internal void RefreshElapsed()
        {
            ElapsedText = Localization.GetLocalizedString(
                "WEBCAP_STOP_ELAPSED",
                _elapsedStopwatch.Elapsed.ToString(@"mm\:ss"));
            RaisePropertyChange(nameof(ElapsedText));
        }

        private static string ProgressPhaseLocalizationKey(
            WebProxyStopPhase phase)
        {
            return phase switch
            {
                WebProxyStopPhase.StoppingPublicListener =>
                    "WEBCAP_STOP_PHASE_PUBLIC",
                WebProxyStopPhase.StoppingInterceptedListener =>
                    "WEBCAP_STOP_PHASE_INTERCEPTED",
                WebProxyStopPhase.WaitingForCallbacks =>
                    "WEBCAP_STOP_PHASE_CALLBACKS",
                WebProxyStopPhase.DrainingUiUpdates =>
                    "WEBCAP_STOP_PHASE_UI",
                WebProxyStopPhase.DisablingSystemProxy =>
                    "WEBCAP_STOP_PHASE_SYSTEM_PROXY",
                WebProxyStopPhase.Complete =>
                    "WEBCAP_STOP_PHASE_COMPLETE",
                _ => "WEBCAP_STOP_PHASE_PUBLIC"
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private readonly Stopwatch _elapsedStopwatch = Stopwatch.StartNew();

        private void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
