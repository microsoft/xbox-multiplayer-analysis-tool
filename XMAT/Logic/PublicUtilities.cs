// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace XMAT
{
    // NOTE: making this available to higher level layers that already depend on
    // this component
    public static class PublicUtilities
    {
        private static readonly Logger _appLog = new();
        private static string _appName;

        static PublicUtilities()
        {
            _appLog.InitLog("application.log", LogLevel.DEBUG);
        }

        public static void AppLog(LogLevel level, string log)
        {
            _appLog.Log(0, level, log);
        }

        public static string AppName
        {
            get
            {
                if (string.IsNullOrEmpty(_appName))
                    _appName = Application.Current.FindResource("appName").ToString();
                return _appName;
            }
        }

        public static string StorageDirectoryPath
        {
            get
            {
                var storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", AppName);
                Directory.CreateDirectory(storageDirectory);
                return storageDirectory;
            }
        }

        public static void RestartAsAdmin()
        {
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                Verb = "runas",
                UseShellExecute = true
            };

            Process.Start(psi);

            Application.Current.Shutdown();
        }

        public static string CollectLogs()
        {
            string inPath = Path.Combine(StorageDirectoryPath, "Logs");
            string strOutFileName = $"{AppName}_Logs_{DateTime.Now:yyyyMMdd-hhmmss}.zip";
            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), strOutFileName);
            string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempPath);

            string[] files = Directory.GetFiles(inPath, "*.log");
            foreach (string file in files)
            {
                File.Copy(file, Path.Combine(tempPath, Path.GetFileName(file)), true);
            }

            ZipFile.CreateFromDirectory(tempPath, outPath);
            Directory.Delete(tempPath, true);
            return strOutFileName;
        }

        public static void SafeInvoke(Action func)
        {
            if (Application.Current != null && Application.Current.Dispatcher != null)
            {
                // if we're on the UI thread, call the function directly
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    func();
                }
                else // otherwise marshal it to the UI thread...
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(func));
                }
            }
        }

        public static async Task<IPAddress> ResolveIP4AddressAsync(string hostname)
        {
            try
            {
                var hostEntries = await Dns.GetHostAddressesAsync(hostname);
                foreach(var addr in hostEntries)
                {
                    if(addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr;
                    }
                }
            }
            catch (SocketException ex)
            {
                AppLog(LogLevel.FATAL, $"Could not resolve {hostname}: {ex}");
            }

            return null;
        }

        public static IEnumerable<IPAddress> GetMyIp4Addresses()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return Array.Empty<IPAddress>();
            }

            try
            {
                List<IPAddress> possibleIpAddresses = new();
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.Supports(NetworkInterfaceComponent.IPv4) &&
                        nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        IPInterfaceProperties properties = nic.GetIPProperties();

                        // Connections with a gateway address are ones with internet connections or at least
                        // connected to a legitimate network
                        // Filters out virtual switches (if hyper-v enabled) and VPN connections
                        if (properties.GatewayAddresses.Count == 0)
                        {
                            continue;
                        }
                        foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
                        {
                            if (addressInfo.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                possibleIpAddresses.Add(addressInfo.Address);
                            }
                        }
                    }
                }
                return possibleIpAddresses;
            }
            catch (Exception)
            {
                return Array.Empty<IPAddress>();
            }
        }

        public static byte[] DecodeGzippedData(byte[] gzipData)
        {
            byte[] bytes = new byte[8192];
            byte[] decoded;

            using (var stream = new GZipStream(new MemoryStream(gzipData), CompressionMode.Decompress))
            {
                using (var memory = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = stream.Read(bytes, 0, bytes.Length);
                        if (count > 0)
                        {
                            memory.Write(bytes, 0, count);
                        }
                    }
                    while (count > 0);
                    decoded = memory.ToArray();
                }
            }

            return decoded;
        }

        public static byte[] DecodeDeflatedData(byte[] deflateData)
        {
            byte[] bytes = new byte[8192];
            byte[] decoded;

            using (var stream = new DeflateStream(new MemoryStream(deflateData), CompressionMode.Decompress))
            {
                using (var memory = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = stream.Read(bytes, 0, bytes.Length);
                        if (count > 0)
                        {
                            memory.Write(bytes, 0, count);
                        }
                    }
                    while (count > 0);
                    decoded = memory.ToArray();
                }
            }

            return decoded;
        }

        public static MemoryStream DecompressZipEntryToMemory(ZipArchiveEntry entry)
        {
            var memory = new MemoryStream();
            using (Stream zip = entry.Open())
            {
                zip.CopyTo(memory);
            }
            memory.Seek(0, SeekOrigin.Begin);
            return memory;
        }

        public static event EventHandler BlockingOperationStarted;
        public static event EventHandler BlockingOperationEnded;

        class BlockingOperationDisposable : IDisposable
        {
            private static int RefCount = 0;

            internal BlockingOperationDisposable()
            {
                if (Interlocked.Increment(ref RefCount) == 1)
                {
                    if (BlockingOperationStarted != null)
                    {
                        SafeInvoke(() => BlockingOperationStarted.Invoke(this, null));
                    }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Decrement(ref RefCount) == 0)
                {
                    if (BlockingOperationEnded != null)
                    {
                        SafeInvoke(() => BlockingOperationEnded(this, null));
                    }
                }
            }
        }

        public static IDisposable BlockingOperation()
        {
            return new BlockingOperationDisposable();
        }

        public static string BodyAsText(Dictionary<string, string> headers, byte[] body)
        {
            if (headers.TryGetValue("Content-Encoding", out string encoding))
            {
                switch (encoding.ToLower())
                {
                    case "gzip":
                        try
                        {
                            body = DecodeGzippedData(body);
                        }
                        catch
                        {
                            // header isn't correct, treat the body as something not encoded
                        }
                        break;
                    case "deflate":
                        try
                        {
                            body = DecodeDeflatedData(body);
                        }
                        catch
                        {
                            // header isn't correct, treat the body as something not encoded
                        }
                        break;
                }
            }

            if (headers.TryGetValue("Content-Type", out string type))
            {
                if (type.Contains("json"))
                {
                    try
                    {
                        return Encoding.UTF8.GetString(body);
                    }
                    catch
                    {
                    }
                }
            }

            return Encoding.ASCII.GetString(body);
        }
    }
}
