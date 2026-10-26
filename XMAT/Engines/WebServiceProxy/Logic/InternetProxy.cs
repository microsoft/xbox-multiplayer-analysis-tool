// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static WinInetConnectionOption;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct InternetPerConnOptionList
{
    public int dwSize;
    public IntPtr szConnection;
    public int dwOptionCount;
    public int dwOptionError;
    public IntPtr options;
};

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct WinInetConnectionOption
{
    static readonly int Size;
    public EWinInetConnectionOptions m_Option;
    public WinInetConnectionOptionVal m_Value;
    static WinInetConnectionOption()
    {
        WinInetConnectionOption.Size = Marshal.SizeOf(typeof(WinInetConnectionOption));
    }

    // Nested Types
    [StructLayout(LayoutKind.Explicit)]
    public struct WinInetConnectionOptionVal
    {
        // Fields
        [FieldOffset(0)]
        public System.Runtime.InteropServices.ComTypes.FILETIME m_FileTime;
        [FieldOffset(0)]
        public int m_Int;
        [FieldOffset(0)]
        public IntPtr m_StringPtr;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct WinInetProxyDetails
{
    public int dwAccessType;
    public IntPtr lpszProxy;
    public IntPtr lpszProxyBypass;

    // Paired with the explicit ANSI INTERNET_OPTION_PROXY query below.
    public string GetProxy() => Marshal.PtrToStringAnsi(lpszProxy) ?? string.Empty;
    public string GetProxyBypass() => Marshal.PtrToStringAnsi(lpszProxyBypass) ?? string.Empty;
}

public enum EWinInetOptions : uint
{
    INTERNET_OPTION_REFRESH = 37,
    INTERNET_PER_CONN_PROXY_SERVER = 38,
    INTERNET_OPTION_SETTINGS_CHANGED = 39,
    INTERNET_OPTION_PER_CONNECTION_OPTION = 75
}

public enum EWinInetConnectionOptions
{
    INTERNET_PER_CONN_FLAGS = 1,
    INTERNET_PER_CONN_PROXY_SERVER = 2
}

[Flags]
public enum EWinInetConnectionFlag
{
    PROXY_TYPE_DIRECT = 0x00000001,
    PROXY_TYPE_PROXY = 0x00000002
}

public enum EWinInetCurrentProxyState
{
    INTERNET_OPEN_TYPE_DIRECT = 1,
    INTERNET_OPEN_TYPE_PROXY = 3
}

static class WinInetFuncs
{
    [DllImport("WinInet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternetSetOption(IntPtr hInternet, EWinInetOptions dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("WinInet.dll", EntryPoint = "InternetQueryOptionA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternetQueryOption(IntPtr hInternet, EWinInetOptions dwOption, IntPtr lpBuffer, ref int dwBufferLength);
}

namespace XMAT.WebServiceCapture
{


    class InternetProxy
    {


        public static bool IsEnabled()
            => ReadConfiguration().State == ProxyConfigurationState.Enabled;

        internal static ProxyConfiguration ReadConfiguration()
        {
            var size = 0;
            WinInetFuncs.InternetQueryOption(IntPtr.Zero, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, IntPtr.Zero, ref size);
            if (size < Marshal.SizeOf<WinInetProxyDetails>())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!WinInetFuncs.InternetQueryOption(IntPtr.Zero, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, buffer, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var details = Marshal.PtrToStructure<WinInetProxyDetails>(buffer);
                return details.dwAccessType == (int)EWinInetCurrentProxyState.INTERNET_OPEN_TYPE_PROXY
                    ? ProxyConfiguration.FromSetting(details.GetProxy())
                    : new ProxyConfiguration(ProxyConfigurationState.Disabled);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void Enable(string strIPAddr, UInt16 uiPort)
        {
            SetProxy(new ProxyEndpoint(strIPAddr, uiPort).ToString());
        }

        public static void Disable()
            => SetProxy(null);

        private static void SetProxy(string server)
        {
            var serverPointer = server == null ? IntPtr.Zero : Marshal.StringToHGlobalAuto(server);
            var options = new[]
            {
                new WinInetConnectionOption
                {
                    m_Option = EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS,
                    m_Value = new WinInetConnectionOptionVal
                    {
                        m_Int = (int)(server == null ? EWinInetConnectionFlag.PROXY_TYPE_DIRECT : EWinInetConnectionFlag.PROXY_TYPE_PROXY)
                    }
                },
                new WinInetConnectionOption
                {
                    m_Option = EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_SERVER,
                    m_Value = new WinInetConnectionOptionVal { m_StringPtr = serverPointer }
                }
            };
            var optionSize = Marshal.SizeOf<WinInetConnectionOption>();
            var optionsPointer = IntPtr.Zero;
            var listPointer = IntPtr.Zero;
            try
            {
                optionsPointer = Marshal.AllocCoTaskMem(optionSize * options.Length);
                for (var i = 0; i < options.Length; i++)
                {
                    Marshal.StructureToPtr(options[i], IntPtr.Add(optionsPointer, i * optionSize), false);
                }
                var list = new InternetPerConnOptionList
                {
                    dwSize = Marshal.SizeOf<InternetPerConnOptionList>(),
                    dwOptionCount = options.Length,
                    options = optionsPointer
                };
                listPointer = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPointer, false);
                Check(WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_PER_CONNECTION_OPTION, listPointer, list.dwSize));
                Check(WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0));
                Check(WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0));
            }
            finally
            {
                Marshal.FreeHGlobal(serverPointer);
                Marshal.FreeCoTaskMem(optionsPointer);
                Marshal.FreeCoTaskMem(listPointer);
            }
        }

        private static void Check(bool succeeded)
        {
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
