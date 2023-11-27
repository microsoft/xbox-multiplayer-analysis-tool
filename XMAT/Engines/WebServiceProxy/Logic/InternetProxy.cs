// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
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

    public string GetProxy() => Marshal.PtrToStringUni(lpszProxy) ?? string.Empty;
    public string GetProxyBypass() => Marshal.PtrToStringUni(lpszProxyBypass) ?? string.Empty;
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

    [DllImport("WinInet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternetQueryOption(IntPtr hInternet, EWinInetOptions dwOption, IntPtr lpBuffer, ref int dwBufferLength);
}

namespace XMAT.WebServiceCapture
{
    

    class InternetProxy
    {


        public static bool IsEnabled()
        {
            int size = 0;
            WinInetFuncs.InternetQueryOption(IntPtr.Zero, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, IntPtr.Zero, ref size);

            IntPtr buffer = Marshal.AllocHGlobal(size);
            if (!WinInetFuncs.InternetQueryOption(IntPtr.Zero, EWinInetOptions.INTERNET_PER_CONN_PROXY_SERVER, buffer, ref size))
            {
                return false;
            }

            return Marshal.PtrToStructure<WinInetProxyDetails>(buffer).dwAccessType == (int)EWinInetCurrentProxyState.INTERNET_OPEN_TYPE_PROXY;
        }

        public static void Enable(string strIPAddr, UInt16 uiPort)
        {
            string strProxyAddr = String.Format("{0}:{1}", strIPAddr, uiPort);

            InternetPerConnOptionList list = new InternetPerConnOptionList();
            WinInetConnectionOption[] options = new WinInetConnectionOption[2]
            {
                new WinInetConnectionOption()
                {
                    m_Option = EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS,
                    m_Value = new WinInetConnectionOptionVal { m_Int = (int)EWinInetConnectionFlag.PROXY_TYPE_PROXY }
                },

                new WinInetConnectionOption()
                {
                    m_Option = EWinInetConnectionOptions.INTERNET_PER_CONN_PROXY_SERVER,
                    m_Value = new WinInetConnectionOptionVal { m_StringPtr = Marshal.StringToHGlobalAuto(strProxyAddr) }
                }
            };

            list.dwSize = Marshal.SizeOf(list);
            list.szConnection = IntPtr.Zero;
            list.dwOptionCount = options.Length;
            list.dwOptionError = 0;

            int optSize = Marshal.SizeOf(typeof(WinInetConnectionOption));
            IntPtr optionsPtr = Marshal.AllocCoTaskMem(optSize * options.Length);
            for (int i = 0; i < options.Length; ++i)
            {
                IntPtr opt = IntPtr.Add(optionsPtr, i * optSize);
                Marshal.StructureToPtr(options[i], opt, false);
            }
            list.options = optionsPtr;

            IntPtr ipcoListPtr = Marshal.AllocCoTaskMem((Int32)list.dwSize);
            Marshal.StructureToPtr(list, ipcoListPtr, false);

            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_PER_CONNECTION_OPTION, ipcoListPtr, list.dwSize);
            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

            Marshal.FreeCoTaskMem(optionsPtr);
            Marshal.FreeCoTaskMem(ipcoListPtr);
        }

        public static void Disable()
        {
            InternetPerConnOptionList list = new InternetPerConnOptionList();
            WinInetConnectionOption[] options = new WinInetConnectionOption[1]
            {
                new WinInetConnectionOption()
                {
                    m_Option = EWinInetConnectionOptions.INTERNET_PER_CONN_FLAGS,
                    m_Value = new WinInetConnectionOptionVal { m_Int = (int)EWinInetConnectionFlag.PROXY_TYPE_DIRECT }
                }
            };

            list.dwSize = Marshal.SizeOf(list);
            list.szConnection = IntPtr.Zero;
            list.dwOptionCount = options.Length;
            list.dwOptionError = 0;

            int optSize = Marshal.SizeOf(typeof(WinInetConnectionOption));
            IntPtr optionsPtr = Marshal.AllocCoTaskMem(optSize * options.Length);
            for (int i = 0; i < options.Length; ++i)
            {
                IntPtr opt = IntPtr.Add(optionsPtr, i * optSize);
                Marshal.StructureToPtr(options[i], opt, false);
            }
            list.options = optionsPtr;

            IntPtr ipcoListPtr = Marshal.AllocCoTaskMem((Int32)list.dwSize);
            Marshal.StructureToPtr(list, ipcoListPtr, false);

            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_PER_CONNECTION_OPTION, ipcoListPtr, list.dwSize);
            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            WinInetFuncs.InternetSetOption(IntPtr.Zero, EWinInetOptions.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

            Marshal.FreeCoTaskMem(optionsPtr);
            Marshal.FreeCoTaskMem(ipcoListPtr);
        }
    }
}
