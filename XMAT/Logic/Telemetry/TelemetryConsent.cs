// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace XMAT.Telemetry
{
    internal enum TelemetryConsentState { Missing, Declined, Accepted, Invalid, Unreadable }

    internal sealed class TelemetryConsentStore(Func<object> read, Action<int> write, Action<string> diagnose)
    {
        internal const string RegistryPath = @"Software\Microsoft\Xbox Multiplayer Analysis Tool";
        internal const string ValueName = "TelemetryConsent";

        internal static TelemetryConsentStore ForCurrentUser(Action<string> diagnose) => new(
            () =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                var value = key?.GetValue(ValueName);
                if (value == null)
                {
                    return null;
                }
                return key.GetValueKind(ValueName) == RegistryValueKind.DWord ? value : "InvalidKind";
            },
            value =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
                key.SetValue(ValueName, value, RegistryValueKind.DWord);
                key.Flush();
            }, diagnose);

        internal TelemetryConsentState Read()
        {
            try
            {
                var value = read();
                if (value == null)
                {
                    return TelemetryConsentState.Missing;
                }
                if (value is int number && number is 0 or 1)
                {
                    return number == 1 ? TelemetryConsentState.Accepted : TelemetryConsentState.Declined;
                }
                diagnose("ConsentInvalid");
                return TelemetryConsentState.Invalid;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                diagnose("ConsentReadFailed");
                return TelemetryConsentState.Unreadable;
            }
        }

        internal bool Save(bool accepted)
        {
            try
            {
                write(accepted ? 1 : 0);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                diagnose("ConsentWriteFailed");
                return false;
            }
        }
    }

    internal sealed class TelemetryConsentPreferences
    {
        private readonly TelemetryConsentStore _store;
        private readonly Action _stop;
        private readonly Action _start;
        private readonly object _gate = new();
        private TelemetryConsentState _state;

        internal TelemetryConsentPreferences(TelemetryConsentStore store, Action stop, Action start)
        {
            _store = store;
            _stop = stop;
            _start = start;
            _state = store.Read();
        }

        internal TelemetryConsentState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        internal bool PromptIfMissing(Func<bool?> prompt) =>
            State != TelemetryConsentState.Missing || Apply(prompt());

        internal bool Apply(bool? accepted)
        {
            lock (_gate)
            {
                if (!accepted.HasValue ||
                    (accepted.Value && _state == TelemetryConsentState.Accepted) ||
                    (!accepted.Value && _state == TelemetryConsentState.Declined))
                {
                    return true;
                }
                // Revoke before touching storage, including when a registry write fails.
                _state = TelemetryConsentState.Declined;
                _stop();
                if (!_store.Save(accepted.Value))
                {
                    _state = TelemetryConsentState.Unreadable;
                    return false;
                }
                _state = accepted.Value ? TelemetryConsentState.Accepted : TelemetryConsentState.Declined;
                if (accepted.Value)
                {
                    _start();
                }
                return true;
            }
        }
    }
}
