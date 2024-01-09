// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT
{
    static class Localization
    {
        private static Dictionary<string, string> g_StringLookupTable = new Dictionary<string, string>();

        public static void LoadLanguage(string strLangCode)
        {
            // TODO_PHIFARQ: Error handling, we could just do nothing and let the XAML defined defaults display
            string strPathToLang = Path.Combine("langs", strLangCode + ".json");

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
