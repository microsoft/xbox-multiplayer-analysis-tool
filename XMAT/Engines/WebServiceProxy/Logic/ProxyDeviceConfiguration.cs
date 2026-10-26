// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture
{
    internal sealed record ProxyEndpoint(string Host, UInt16 Port)
    {
        internal bool Matches(ProxyEndpoint other) =>
            other != null && Port == other.Port &&
            string.Equals(NormalizeHost(Host), NormalizeHost(other.Host), StringComparison.OrdinalIgnoreCase);

        internal static string NormalizeHost(string host) =>
            IPAddress.TryParse(host?.Trim().Trim('[', ']'), out var address)
                ? address.ToString()
                : host?.Trim().TrimEnd('.');

        internal static bool TryParse(string value, out ProxyEndpoint endpoint)
        {
            endpoint = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(7);
            }
            else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(8);
            }
            value = value.TrimEnd('/');
            var separator = value.LastIndexOf(':');
            if (separator <= 0 ||
                !UInt16.TryParse(value.AsSpan(separator + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var port) || port == 0)
            {
                return false;
            }
            var host = value.Substring(0, separator).Trim('[', ']');
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                return false;
            }
            endpoint = new ProxyEndpoint(host, port);
            return true;
        }

        public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }

    internal enum ProxyConfigurationState
    {
        Disabled,
        Enabled,
        Unreachable,
        Unknown
    }

    internal sealed record ProxyConfiguration(
        ProxyConfigurationState State, ProxyEndpoint Endpoint = null, string Detail = null)
    {
        internal static ProxyConfiguration FromSetting(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting))
            {
                return new(ProxyConfigurationState.Disabled);
            }
            return ProxyEndpoint.TryParse(setting, out var endpoint)
                ? new(ProxyConfigurationState.Enabled, endpoint)
                : new(ProxyConfigurationState.Unknown, Detail: setting);
        }

        internal void RequireKnown()
        {
            if (State is ProxyConfigurationState.Unknown or ProxyConfigurationState.Unreachable)
            {
                throw new InvalidOperationException(
                    Localization.GetLocalizedString("PROXY_CONFIGURATION_UNKNOWN", Detail ?? State.ToString()));
            }
        }
    }

    internal interface IProxyDeviceConfiguration
    {
        Task<ProxyConfiguration> ReadAsync();
        Task DisableAsync();
        Task EnableAsync(ProxyEndpoint endpoint);
    }

    internal sealed class XboxProxyDeviceConfiguration : IProxyDeviceConfiguration
    {
        private readonly string _device;

        internal XboxProxyDeviceConfiguration(string device) => _device = device;

        public Task<ProxyConfiguration> ReadAsync() => Task.Run(() =>
        {
            try
            {
                if (!XboxClient.XboxClientConnection.IsOnline(_device))
                {
                    return new ProxyConfiguration(ProxyConfigurationState.Unreachable);
                }
                return ProxyConfiguration.FromSetting(XboxClient.XboxClientConnection.GetProxySetting(_device));
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not read console proxy configuration: {ex}");
                return new ProxyConfiguration(ProxyConfigurationState.Unknown, Detail: ex.GetBaseException().Message);
            }
        });

        public async Task DisableAsync()
        {
            if (!await CaptureUtilities.DisableXboxProxyAsync(_device))
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_ERROR_CONNECTION_DESC"));
            }
        }

        public async Task EnableAsync(ProxyEndpoint endpoint)
        {
            if (!await CaptureUtilities.EnableXboxProxyAsync(_device, endpoint.Host, endpoint.Port))
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_ERROR_CONNECTION_DESC"));
            }
        }
    }

    internal sealed class PcProxyDeviceConfiguration : IProxyDeviceConfiguration
    {
        public Task<ProxyConfiguration> ReadAsync() => Task.Run(InternetProxy.ReadConfiguration);
        public Task DisableAsync() => Task.Run(InternetProxy.Disable);
        public Task EnableAsync(ProxyEndpoint endpoint) =>
            Task.Run(() => InternetProxy.Enable(endpoint.Host, endpoint.Port));
    }

    internal static class ProxyDeviceOperations
    {
        private static readonly object Sync = new();
        private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase);

        internal static async Task<IDisposable> AcquireAsync(DeviceType type, string device)
        {
            var target = type == DeviceType.LocalPC ? "LocalPC" : device;
            if (type == DeviceType.XboxConsole)
            {
                var address = await PublicUtilities.ResolveIP4AddressAsync(device);
                if (address == null)
                {
                    throw new InvalidOperationException(
                        Localization.GetLocalizedString("DNS_RESOLVE_ERROR_MESSAGE", device));
                }
                target = address.ToString();
            }
            return AcquireTarget(target);
        }

        internal static IDisposable AcquireTarget(string target)
        {
            lock (Sync)
            {
                if (!Targets.Add(target))
                {
                    throw new InvalidOperationException(
                        Localization.GetLocalizedString("PROXY_DEVICE_BUSY", target));
                }
            }
            return new TargetLease(target);
        }

        private sealed class TargetLease : IDisposable
        {
            private string _target;
            internal TargetLease(string target) => _target = target;
            public void Dispose()
            {
                lock (Sync)
                {
                    if (_target != null)
                    {
                        Targets.Remove(_target);
                        _target = null;
                    }
                }
            }
        }
    }

    internal sealed class ProxyPortChangeOperation
    {
        private readonly IProxyDeviceConfiguration _device;
        private readonly Func<Task> _start;
        private readonly Func<Task> _stop;
        private readonly Action _commit;
        private readonly Action<string> _status;
        private readonly Func<CancellationToken, Task> _delay;
        private readonly TimeSpan _timeout;

        internal ProxyPortChangeOperation(
            IProxyDeviceConfiguration device, Func<Task> start, Func<Task> stop,
            Action commit, Action<string> status,
            Func<CancellationToken, Task> delay = null, TimeSpan? timeout = null)
        {
            _device = device;
            _start = start;
            _stop = stop;
            _commit = commit;
            _status = status;
            _delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(2), token));
            _timeout = timeout ?? TimeSpan.FromMinutes(3);
        }

        internal async Task ApplyAsync(
            Func<ProxyEndpoint, bool> ownsCurrent, ProxyEndpoint desired,
            CancellationToken cancellationToken)
        {
            var current = await WaitAsync(_ => true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            current.RequireKnown();
            if (current.State == ProxyConfigurationState.Enabled &&
                !current.Endpoint.Matches(desired) && !ownsCurrent(current.Endpoint))
            {
                throw new InvalidOperationException(
                    Localization.GetLocalizedString("PROXY_ENDPOINT_CONFLICT", current.Endpoint, desired));
            }

            await _start();
            var committed = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.State == ProxyConfigurationState.Enabled && current.Endpoint.Matches(desired))
                {
                    _commit();
                    committed = true;
                }
                else
                {
                    if (current.State == ProxyConfigurationState.Enabled)
                    {
                        _status("PROXY_PORT_DISABLING");
                        await _device.DisableAsync();
                        await WaitAsync(config => config.State == ProxyConfigurationState.Disabled, cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    _commit();
                    committed = true;
                    _status("PROXY_PORT_ENABLING");
                    cancellationToken.ThrowIfCancellationRequested();
                    await _device.EnableAsync(desired);
                    await WaitAsync(config => config.State == ProxyConfigurationState.Enabled &&
                        config.Endpoint?.Matches(desired) == true, cancellationToken);
                }
                _status("PROXY_PORT_APPLIED");
            }
            catch
            {
                try
                {
                    // Once committed, a partially configured device may already use this listener.
                    if (!committed)
                    {
                        await _stop();
                    }
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Port-change listener cleanup failed: {ex}");
                }
                throw;
            }
        }

        private async Task<ProxyConfiguration> WaitAsync(
            Func<ProxyConfiguration, bool> predicate, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            ProxyConfiguration configuration = null;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = _timeout - stopwatch.Elapsed;
                    configuration = await _device.ReadAsync().WaitAsync(
                        remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (predicate(configuration))
                    {
                        return configuration;
                    }
                    remaining = _timeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException();
                    }
                    await _delay(cancellationToken).WaitAsync(remaining, cancellationToken);
                }
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    Localization.GetLocalizedString("PROXY_CONFIGURATION_TIMEOUT",
                        configuration?.Detail ?? (configuration?.State ?? ProxyConfigurationState.Unknown).ToString()),
                    ex);
            }
        }
    }
}
