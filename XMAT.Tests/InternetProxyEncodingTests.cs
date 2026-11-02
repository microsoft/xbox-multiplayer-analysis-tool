// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class InternetProxyEncodingTests
    {
        [Theory]
        [InlineData("127.0.0.1:8880", "<local>")]
        [InlineData("proxy.example:8080", "localhost;*.example")]
        [InlineData("", "")]
        public void ProxyInfo_DecodesAnsiCStringWithoutReadingAdjacentData(string proxy, string bypass)
        {
            var bytes = Encoding.ASCII.GetBytes($"{proxy}\0{proxy}\0{bypass}\0");
            var buffer = Marshal.AllocHGlobal(bytes.Length + 16);
            try
            {
                Marshal.Copy(new byte[bytes.Length + 16], 0, buffer, bytes.Length + 16);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var details = new WinInetProxyDetails
                {
                    lpszProxy = buffer,
                    lpszProxyBypass = IntPtr.Add(buffer, (proxy.Length + 1) * 2)
                };
                Assert.Equal(proxy, details.GetProxy());
                Assert.Equal(bypass, details.GetProxyBypass());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ProxyInfo_NullPointersRemainEmpty()
        {
            var details = new WinInetProxyDetails();
            Assert.Equal(string.Empty, details.GetProxy());
            Assert.Equal(string.Empty, details.GetProxyBypass());
        }

        [Fact]
        public void Query_UsesExplicitAnsiEntryPoint_WithoutChangingUnicodeSetter()
        {
            var query = typeof(WinInetFuncs).GetMethod(nameof(WinInetFuncs.InternetQueryOption))
                .GetCustomAttributes(typeof(DllImportAttribute), false).Cast<DllImportAttribute>().Single();
            Assert.Equal("InternetQueryOptionA", query.EntryPoint);
            Assert.Equal(CharSet.Ansi, query.CharSet);
            Assert.True(query.ExactSpelling);
            var setter = typeof(WinInetFuncs).GetMethod(nameof(WinInetFuncs.InternetSetOption))
                .GetCustomAttributes(typeof(DllImportAttribute), false).Cast<DllImportAttribute>().Single();
            Assert.Equal(CharSet.Auto, setter.CharSet);
            Assert.Equal("InternetSetOption", setter.EntryPoint);
        }

        [Fact]
        public void NativePrivateHandle_ReturnsReadableProxyWithoutChangingSystemSettings()
        {
            var handle = InternetOpen("XMAT-proxy-encoding-test", 3, "127.0.0.1:8880", "<local>", 0);
            Assert.NotEqual(IntPtr.Zero, handle);
            var buffer = IntPtr.Zero;
            try
            {
                var size = 0;
                WinInetFuncs.InternetQueryOption(handle, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, IntPtr.Zero, ref size);
                Assert.True(size >= Marshal.SizeOf<WinInetProxyDetails>());
                buffer = Marshal.AllocHGlobal(size);
                Assert.True(WinInetFuncs.InternetQueryOption(
                    handle, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, buffer, ref size));
                var details = Marshal.PtrToStructure<WinInetProxyDetails>(buffer);
                Assert.Equal(3, details.dwAccessType);
                Assert.Equal("127.0.0.1:8880", details.GetProxy());
                Assert.Contains("<local>", details.GetProxyBypass(), StringComparison.Ordinal);
                var configuration = ProxyConfiguration.FromSetting(details.GetProxy());
                Assert.Equal(ProxyConfigurationState.Enabled, configuration.State);
                Assert.Equal((ushort)8880, configuration.Endpoint.Port);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Assert.True(InternetCloseHandle(handle));
            }
        }

        [DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr InternetOpen(string agent, int access, string proxy, string bypass, int flags);

        [DllImport("wininet.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetCloseHandle(IntPtr handle);
    }
}
