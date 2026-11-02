// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using XboxClient;
using static XboxClient.XboxClientConnection;

namespace XMAT.WebServiceCapture
{
    internal sealed class ProxyActivationUncertainException : InvalidOperationException
    {
        internal ProxyActivationUncertainException(int port, Exception innerException)
            : base(Localization.GetLocalizedString(
                "PROXY_ENABLE_UNCERTAIN", port, innerException.GetBaseException().Message), innerException)
        {
        }

        internal ProxyActivationUncertainException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static class CaptureUtilities
    {
        private static string CertFile = "XMATRoot.cer"; // Locally we use XMAT... but on the box we need to user FiddlerRoot
        private static string XboxCertPath = "xs:\\microsoft\\cert\\FiddlerRoot.cer";
        private static string XboxConfigPath = "xs:\\microsoft\\fiddler\\ProxyAddress.txt";

        internal static string GetProxyOperationErrorMessage(Exception ex) =>
            ex is ProxyActivationUncertainException or ProxyRecoveryException
                ? ex.Message
                : ex.GetBaseException().Message;

        internal static string GetPath(string file)
        {
            return Path.Combine(PublicUtilities.StorageDirectoryPath, file);
        }

        internal static byte[] EncodeXboxProxyAddress(string proxyAddress, int port)
        {
            // The console's loader consumes a C string; EOF or CRLF alone can
            // leave trailing memory in httpproxyhost.
            return Encoding.UTF8.GetBytes($"{proxyAddress}:{port}\0");
        }

        internal static Task<bool> EnableXboxProxyAsync(string ipAddress, string proxyAddr, int port)
        {
            return EnableXboxProxyAsync(proxyAddr, port,
                () => XboxClientConnection.IsOnline(ipAddress),
                (source, destination) => XboxClientConnection.CopyFile(ipAddress, source, destination),
                () => XboxClientConnection.Reboot(ipAddress));
        }

        internal static Task<bool> EnableXboxProxyAsync(
            string proxyAddr, int port, Func<bool> isOnline,
            Action<string, string> copyFile, Action reboot)
        {
            return Task.Run(() =>
            {
                var configFile = Path.GetTempFileName();
                var configurationWriteAttempted = false;
                try
                {
                    File.WriteAllBytes(configFile, EncodeXboxProxyAddress(proxyAddr, port));
                    if (!isOnline())
                    {
                        return false;
                    }
                    copyFile(GetPath(CertFile), XboxCertPath);
                    configurationWriteAttempted = true;
                    copyFile(configFile, XboxConfigPath);
                    reboot();
                    return true;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    var failure = ex is TargetInvocationException { InnerException: not null }
                        ? ex.InnerException : ex;
                    if (configurationWriteAttempted)
                    {
                        throw new ProxyActivationUncertainException(port, failure);
                    }
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    throw;
                }
                finally
                {
                    try
                    {
                        File.Delete(configFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        PublicUtilities.AppLog(LogLevel.ERROR, $"Could not remove proxy configuration temporary file: {ex}");
                    }
                }
            });
        }

        internal static Task<bool> DisableXboxProxyAsync(string ipAddress)
        {
            return Task.Run(() =>
            {
                if (XboxClientConnection.IsOnline(ipAddress))
                {
                    XboxClientConnection.DeleteFile(ipAddress, XboxCertPath);
                    XboxClientConnection.DeleteFile(ipAddress, XboxConfigPath);
                    XboxClientConnection.Reboot(ipAddress);
                    return true;
                }
                else
                {
                    return false;
                }
            });
        }

        internal static Task<EProxyEnabledCheckResult> IsXboxProxyEnabledAsync(string ipAddress)
        {
            return Task.Run(() =>
            {
                EProxyEnabledCheckResult result = EProxyEnabledCheckResult.ConsoleUnreachable;
                if (XboxClientConnection.IsOnline(ipAddress))
                {
                    result = XboxClientConnection.IsProxyEnabled(ipAddress);
                }
                return result;
            });
        }
    }
}
