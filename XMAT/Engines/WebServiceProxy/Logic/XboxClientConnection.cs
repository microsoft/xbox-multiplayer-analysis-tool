// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Reflection;
using System.Net;

public static class GDKXHelper
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern int GetPrivateProfileString(string Section, string Key, string Default, System.Text.StringBuilder RetVal, int Size, string FilePath);


    public static bool IsGDKXInstalled(bool bShowErrorMessage)
    {
        bool bFoundGDK = false;

        // find GDK path first
        if (GetGDKXPath(out string gdkxVersion) != null)
        {
            bFoundGDK = true;
        }

        if (!bFoundGDK)
        {
            if (bShowErrorMessage)
            {
                MessageBox.Show(XMAT.Localization.GetLocalizedString("GDKX_NEEDED_MESSAGE"), XMAT.Localization.GetLocalizedString("GDKX_NEEDED_TITLE"), MessageBoxButton.OK, MessageBoxImage.Hand);
            }
        }

        return bFoundGDK;
    }

    public static string GetGDKXPath(out string gdkxVersion)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GDK\Installed Roots"))
        {
            if (key?.GetValue("GDKInstallPath") is string gdkPath)
            {
                // Is it a GDKX install?
                string strToolsINIPath = Path.Combine(gdkPath, "bin", "gxdkTools.ini");
                if (File.Exists(strToolsINIPath))
                {
                    // read out the version
                    var gdkxVersionSB = new StringBuilder(255);
                    GetPrivateProfileString("gxdkedition", "_xbld_name", "", gdkxVersionSB, 255, strToolsINIPath);
                    gdkxVersion = gdkxVersionSB.ToString();
                    return gdkPath;
                }
            }
        }

        gdkxVersion = null;
        return null;
    }

    public static Assembly LoadToolsAssembly(string strDLL)
    {
        if (!IsGDKXInstalled(false))
        {
            return null;
        }

        return Assembly.LoadFrom(Path.Combine(GDKXHelper.GetGDKXPath(out string gdkxVersion), "bin", strDLL));
    }

    public static void CreateConsoleControlClient(string ipAddress, out Assembly loadedAssembly, out Type ConsoleControlClientType, out object ConsoleControlClientInstance)
    {
        loadedAssembly = GDKXHelper.LoadToolsAssembly("Microsoft.Xbox.Xtf.ConsoleControl.dll");
        ConsoleControlClientType = loadedAssembly.GetType("Microsoft.Xbox.XTF.Console.ConsoleControlClient");
        ConsoleControlClientInstance = Activator.CreateInstance(ConsoleControlClientType, new object[] { ipAddress });
    }

    public static void CreateConsoleManager(out Assembly loadedAssembly, out Type ConsoleManagerType, out object ConsoleManagerInstance)
    {
        loadedAssembly = GDKXHelper.LoadToolsAssembly("Microsoft.Xbox.Xtf.ConsoleManager.dll");
        ConsoleManagerType = loadedAssembly.GetType("Microsoft.Xbox.XTF.Console.ConsoleManager");
        ConsoleManagerInstance = Activator.CreateInstance(ConsoleManagerType);
    }

    public static void CreateFileIOClient(string ipAddress, out Assembly loadedAssembly, out Type FileIOClientType, out object FileIOClientInstance)
    {
        loadedAssembly = GDKXHelper.LoadToolsAssembly("Microsoft.Xbox.Xtf.FileIO.dll");
        FileIOClientType = loadedAssembly.GetType("Microsoft.Xbox.XTF.IO.FileIOClient");
        FileIOClientInstance = Activator.CreateInstance(FileIOClientType, new object[] { ipAddress });
    }
}

namespace XboxClient
{
    public static class XboxClientConnection
    {
        public static string GetDefaultConsoleAddress()
        {
            try
            {
                GDKXHelper.CreateConsoleManager(out Assembly loadedAssembly, out Type ConsoleManagerType, out object ConsoleManagerInstance);

                MethodInfo method = ConsoleManagerType.GetMethod("GetDefaultConsole", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object objDefaultConsole = method.Invoke(ConsoleManagerInstance, null);

                if (objDefaultConsole != null)
                {
                    // XTF console type which is returned from GetDefaultConsole
                    Type xtfConsoleType = loadedAssembly.GetType("Microsoft.Xbox.XTF.Console.XtfConsole");

                    // get the address property
                    PropertyInfo prop = xtfConsoleType.GetProperty("Address");
                    object propVal = prop.GetValue(objDefaultConsole);
                    if (propVal != null)
                    {
                        string strDefaultConsoleAddr = (string)propVal;
                        return strDefaultConsoleAddr;
                    }
                }
            }
            catch
            {
                return "";
            }

            return "";
        }

        public static bool FileExists(string ipAddress, string path)
        {
            try
            {
                GDKXHelper.CreateFileIOClient(ipAddress, out Assembly loadedAssembly, out Type FileIOClientType, out object FileIOClientInstance);

                MethodInfo method = FileIOClientType.GetMethod("GetFileInfo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object objFileInfo = method.Invoke(FileIOClientInstance, new object[] { path });
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        public static void CopyFile(string ipAddress, string srcPath, string destPath)
        {
            try
            {
                GDKXHelper.CreateFileIOClient(ipAddress, out Assembly loadedAssembly, out Type FileIOClientType, out object FileIOClientInstance);

                Type enumType = loadedAssembly.GetType("Microsoft.Xbox.XTF.IO.CopyFileFlags");
                object enumFlag = Convert.ChangeType(0x00000000, Enum.GetUnderlyingType(enumType));

                MethodInfo method = FileIOClientType.GetMethod("CopyFiles", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                method.Invoke(FileIOClientInstance, new object[] { srcPath, (FileAttributes)(-1), FileAttributes.System | FileAttributes.Hidden, (uint)1, destPath, enumFlag, null, null });
            }
            catch
            {

            }
        }

        public static void DeleteFile(string ipAddress, string path)
        {
            try
            {
                GDKXHelper.CreateFileIOClient(ipAddress, out Assembly loadedAssembly, out Type FileIOClientType, out object FileIOClientInstance);

                Type enumType = loadedAssembly.GetType("Microsoft.Xbox.XTF.IO.DeleteFileFlags");
                object enumFlag = Convert.ChangeType(0x00000000, Enum.GetUnderlyingType(enumType));

                MethodInfo method = FileIOClientType.GetMethod("DeleteFiles", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                method.Invoke(FileIOClientInstance, new object[] { path, (FileAttributes)(-1), FileAttributes.System | FileAttributes.Hidden, (uint)1, enumFlag, null });
            }
            catch
            {

            }
        }

        public static void Reboot(string ipAddress)
        {
            try
            {
                GDKXHelper.CreateConsoleControlClient(ipAddress, out Assembly loadedAssembly, out Type ConsoleControlClientType, out object ConsoleControlClientInstance);

                // get the ShutdownConsoleFlags type
                Type enumType = loadedAssembly.GetType("Microsoft.Xbox.XTF.Console.ShutdownConsoleFlags");
                object enumFlag = Convert.ChangeType(0x00000001, Enum.GetUnderlyingType(enumType));

                MethodInfo method = ConsoleControlClientType.GetMethod("ShutdownConsole", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                method.Invoke(ConsoleControlClientInstance, new object[] { enumFlag });
            }
            catch
            {

            }
        }

        // TODO: Better handling of when the console is offline. Enable proxy for example shows no error message
        public static bool IsOnline(string ipAddress)
        {
            try
            {
                GDKXHelper.CreateConsoleControlClient(ipAddress, out Assembly loadedAssembly, out Type ConsoleControlClientType, out object ConsoleControlClientInstance);

                PropertyInfo prop = ConsoleControlClientType.GetProperty("SystemTime");
                object propVal = prop.GetValue(ConsoleControlClientInstance);
                if (propVal != null)
                {
                    DateTime now = (DateTime)propVal;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public enum EProxyEnabledCheckResult
        {
            DeviceIsXbox_NoGDKX,
            ConsoleUnreachable,
            ProxyDisabled,
            ProxyEnabled
        }
        public static EProxyEnabledCheckResult IsProxyEnabled(string ipAddress)
        {
            try
            {
                GDKXHelper.CreateConsoleControlClient(ipAddress, out Assembly loadedAssembly, out Type ConsoleControlClientType, out object ConsoleControlClientInstance);

                MethodInfo method = ConsoleControlClientType.GetMethod("GetSetting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object retVal = method.Invoke(ConsoleControlClientInstance, new object[] { "httpproxyhost" });
                string proxyVal = (string)retVal;
                return String.IsNullOrEmpty(proxyVal) ? EProxyEnabledCheckResult.ProxyDisabled : EProxyEnabledCheckResult.ProxyEnabled;
            }
            catch
            {
                return EProxyEnabledCheckResult.ConsoleUnreachable;
            }
        }
    }
}
