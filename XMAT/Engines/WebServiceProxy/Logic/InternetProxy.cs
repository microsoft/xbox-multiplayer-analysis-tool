// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Linq;
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
    INTERNET_PER_CONN_PROXY_SERVER = 2,
    INTERNET_PER_CONN_PROXY_BYPASS = 3,
    INTERNET_PER_CONN_AUTOCONFIG_URL = 4,
    INTERNET_PER_CONN_FLAGS_UI = 10
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

    [DllImport("WinInet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternetQueryOptionUnicode(IntPtr hInternet, EWinInetOptions dwOption, IntPtr lpBuffer, ref int dwBufferLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);
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

        // Journals a lease and starts the crash guard before the Windows proxy changes.
        public static void Enable(string strIPAddr, UInt16 uiPort)
            => PcProxyRecovery.Current.Enable(new ProxyEndpoint(strIPAddr, uiPort));

        // Stops capturing this PC: restores the settings from before capture when they are unchanged, otherwise turns
        // the proxy off only while it uses XMAT's own endpoint. Throws when it uses someone else's proxy, which is
        // kept, or when the result does not read back as written, keeping the recovery lease.
        public static void Disable()
            => PcProxyRecovery.Current.Disable();

        // The same, also turning off the given endpoint, the one this capture configured, when nothing is journaled.
        public static void Disable(string strIPAddr, UInt16 uiPort)
            => PcProxyRecovery.Current.Disable(new ProxyEndpoint(strIPAddr, uiPort));

        internal static ProxySettings ReadSettings()
        {
            // Windows 7 and later report auto-detect only through FLAGS_UI; older systems reject it.
            return QuerySettings(EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS_UI, out _)
                ?? QuerySettings(EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS, out var error)
                ?? throw new Win32Exception(error);
        }

        internal static void WriteSettings(ProxySettings settings)
        {
            var strings = new[] { settings.Server, settings.Bypass, settings.AutoConfigUrl }
                .Select(value => value == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(value))
                .ToArray();
            try
            {
                SetOptions(
                    Option(EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS, new WinInetConnectionOptionVal { m_Int = settings.Flags }),
                    Option(EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_SERVER, new WinInetConnectionOptionVal { m_StringPtr = strings[0] }),
                    Option(EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_BYPASS, new WinInetConnectionOptionVal { m_StringPtr = strings[1] }),
                    Option(EWinInetConnectionOptions.INTERNET_PER_CONN_AUTOCONFIG_URL, new WinInetConnectionOptionVal { m_StringPtr = strings[2] }));
            }
            finally
            {
                foreach (var pointer in strings)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }

        private static WinInetConnectionOption Option(EWinInetConnectionOptions option, WinInetConnectionOptionVal value)
            => new() { m_Option = option, m_Value = value };

        private static ProxySettings QuerySettings(EWinInetConnectionOptions flagsOption, out int error)
        {
            var options = new[]
            {
                Option(flagsOption, default),
                Option(EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_SERVER, default),
                Option(EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_BYPASS, default),
                Option(EWinInetConnectionOptions.INTERNET_PER_CONN_AUTOCONFIG_URL, default)
            };
            var optionSize = Marshal.SizeOf<WinInetConnectionOption>();
            var optionsPointer = IntPtr.Zero;
            var listPointer = IntPtr.Zero;
            try
            {
                optionsPointer = WriteOptions(options);
                var list = new InternetPerConnOptionList
                {
                    dwSize = Marshal.SizeOf<InternetPerConnOptionList>(),
                    dwOptionCount = options.Length,
                    options = optionsPointer
                };
                listPointer = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPointer, false);
                var size = list.dwSize;
                if (!WinInetFuncs.InternetQueryOptionUnicode(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_PER_CONNECTION_OPTION, listPointer, ref size))
                {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }
                error = 0;
                for (var i = 0; i < options.Length; i++)
                {
                    options[i] = Marshal.PtrToStructure<WinInetConnectionOption>(IntPtr.Add(optionsPointer, i * optionSize));
                }
                return new ProxySettings(
                    options[0].m_Value.m_Int, TakeString(options[1]), TakeString(options[2]), TakeString(options[3]));
            }
            finally
            {
                Marshal.FreeCoTaskMem(optionsPointer);
                Marshal.FreeCoTaskMem(listPointer);
            }
        }

        // WinINet allocates queried strings; each one is released with GlobalFree.
        private static string TakeString(WinInetConnectionOption option)
        {
            var pointer = option.m_Value.m_StringPtr;
            if (pointer == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                WinInetFuncs.GlobalFree(pointer);
            }
        }

        private static IntPtr WriteOptions(WinInetConnectionOption[] options)
        {
            var optionSize = Marshal.SizeOf<WinInetConnectionOption>();
            var optionsPointer = Marshal.AllocCoTaskMem(optionSize * options.Length);
            for (var i = 0; i < options.Length; i++)
            {
                Marshal.StructureToPtr(options[i], IntPtr.Add(optionsPointer, i * optionSize), false);
            }
            return optionsPointer;
        }

        private static void SetOptions(params WinInetConnectionOption[] options)
        {
            var optionsPointer = IntPtr.Zero;
            var listPointer = IntPtr.Zero;
            try
            {
                optionsPointer = WriteOptions(options);
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
