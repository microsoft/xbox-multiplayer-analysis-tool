// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using XboxClient;
using static XboxClient.XboxClientConnection;

namespace XMAT.WebServiceCapture
{
    internal static class CaptureUtilities
    {
        private static string CertFile = "XMATRoot.cer"; // Locally we use XMAT... but on the box we need to user FiddlerRoot
        private static string XboxCertPath = "xs:\\microsoft\\cert\\FiddlerRoot.cer";
        private static string XboxConfigPath = "xs:\\microsoft\\fiddler\\ProxyAddress.txt";

        internal static string GetPath(string file)
        {
            return Path.Combine(PublicUtilities.StorageDirectoryPath, file);
        }

        internal static Task<bool> EnableXboxProxyAsync(string ipAddress, string proxyAddr, int port)
        {
            return Task.Run(() =>
            {
                var configFile = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(configFile, $"{proxyAddr}:{port}");
                    if (!XboxClientConnection.IsOnline(ipAddress))
                    {
                        return false;
                    }
                    XboxClientConnection.CopyFile(ipAddress, GetPath(CertFile), XboxCertPath);
                    XboxClientConnection.CopyFile(ipAddress, configFile, XboxConfigPath);
                    XboxClientConnection.Reboot(ipAddress);
                    return true;
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
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
