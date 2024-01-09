// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using System.Threading.Tasks;
using XboxClient;
using static XboxClient.XboxClientConnection;

namespace XMAT.WebServiceCapture
{
    internal static class CaptureUtilities
    {
        private static string CertFile = "XMATRoot.cer"; // Locally we use XMAT... but on the box we need to user FiddlerRoot
        private static string ConfigFile = "ProxyAddress.txt";
        private static string XboxCertPath = "xs:\\microsoft\\cert\\FiddlerRoot.cer";
        private static string XboxConfigPath = "xs:\\microsoft\\fiddler\\ProxyAddress.txt";

        internal static string GetPath(string file)
        {
            return Path.Combine(PublicUtilities.StorageDirectoryPath, file);
        }

        internal static Task<bool> EnableXboxProxyAsync(string ipAddress, string proxyAddr, int port)
        {
            var proxyFileContents = $"{proxyAddr}:{port}";
            File.WriteAllText(GetPath(ConfigFile), proxyFileContents);

            return Task.Run(() =>
            {
                if (XboxClientConnection.IsOnline(ipAddress))
                {
                    XboxClientConnection.CopyFile(ipAddress, GetPath(CertFile), XboxCertPath);
                    XboxClientConnection.CopyFile(ipAddress, GetPath(ConfigFile), XboxConfigPath);
                    XboxClientConnection.Reboot(ipAddress);
                    return true;
                }
                else
                {
                    return false;
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
