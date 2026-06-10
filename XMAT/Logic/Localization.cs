// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XMAT
{
    static class Localization
    {
        private static Dictionary<string, string> g_StringLookupTable = new Dictionary<string, string>();

        public static void LoadLanguage(string strLangCode)
        {
            // TODO_PHIFARQ: Error handling, we could just do nothing and let the XAML defined defaults display
            if (String.IsNullOrWhiteSpace(strLangCode))
            {
                return;
            }

            string strSafeLangCode = Path.GetFileName(strLangCode);
            if (!String.Equals(strSafeLangCode, strLangCode, StringComparison.Ordinal))
            {
                return;
            }

            string strLangsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "langs"));
            string strPathToLang = Path.GetFullPath(Path.Combine(strLangsDirectory, strSafeLangCode + ".json"));
            string strLangsDirectoryWithSeparator = strLangsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!strPathToLang.StartsWith(strLangsDirectoryWithSeparator, StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(strPathToLang))
            {
                try
                {
                    g_StringLookupTable = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(strPathToLang));
                }
                catch (IOException)
                {
                    return;
                }
            }
        }

        public static string GetLocalizedString(string strKey)
        {
            return GetLocalizedString(strKey, new object { });
        }

        public static string GetLocalizedString(string strKey, params object[] strParams)
        {
            if (g_StringLookupTable.ContainsKey(strKey))
            {
                return String.Format(g_StringLookupTable[strKey], strParams);
            }

            return String.Format("NO_IMPL_{0}", strKey);
        }
    }
}
