// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Operations.ParityFixtures
{
    // Code in a headless namespace that uses each member HeadlessBans forbids there, for the scanner self-test. Nothing
    // here runs. LanguageLoading shows that loading a language is banned as well: only a reviewed exception in
    // HeadlessBans can allow one specific caller.
    internal static class HeadlessBanFixture
    {
        internal static object AppModelSingleton() => CaptureAppModel.AppModel;

        internal static object ActiveDatabase(ICaptureAppModel model) => model.ActiveDatabase;

        internal static bool SavedSettings(CaptureAppModel model) => CaptureAppSettings.Serialize(model);

        internal static object Preferences() => new AppPreferencesModel();

        internal static string StorageFolder() => PublicUtilities.StorageDirectoryPath;

        internal static string ApplicationName() => PublicUtilities.AppName;

        internal static string InstallFolder() => PublicUtilities.InstallDirectoryPath;

        internal static void ApplicationLog() => PublicUtilities.AppLog(LogLevel.INFO, "text");

        internal static string LocalizedText() => Localization.GetLocalizedString("KEY");

        internal static void LanguageLoading() => Localization.LoadLanguage("en-us");
    }
}

namespace XMAT.Tests.HeadlessParity
{
    // The same kind of uses outside the headless namespaces, where the rest of the product may make them: nothing here
    // may be reported. Nothing here runs.
    internal static class HeadlessBanControlFixture
    {
        internal static object AppModelSingleton() => CaptureAppModel.AppModel;

        internal static string StorageFolder() => PublicUtilities.StorageDirectoryPath;

        internal static string LocalizedText() => Localization.GetLocalizedString("KEY");
    }
}
