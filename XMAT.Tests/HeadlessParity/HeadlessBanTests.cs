// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.ParityFixtures;

namespace XMAT.Tests.HeadlessParity
{
    // Test D: the shared operations (XMAT.Operations) and the command line (XMAT.Cli) get the app model, settings,
    // names, storage, logging and text from their caller instead of GUI globals. HeadlessBans holds the rules and any
    // reviewed exceptions; these tests also pass when there are none.
    public class HeadlessBanTests
    {
        [Fact]
        public void HeadlessCode_UsesNoBannedMembers()
        {
            List<string> offenders = ScanSources()
                .Where(use => !HeadlessBans.IsReviewed(use))
                .Select(Describe)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "XMAT.Operations and XMAT.Cli must get these from their caller instead of GUI globals:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // An exception that nothing needs any more is removed, so the list stays short and true.
        [Fact]
        public void ReviewedExceptions_AreStillNeeded()
        {
            List<ForbiddenCall> uses = ScanSources();
            List<string> unused = HeadlessBans.ReviewedUses
                .Where(reviewed => !uses.Any(use => use.Caller == reviewed.Caller && use.Callee == reviewed.Member))
                .Select(reviewed => $"  {reviewed.Caller} no longer uses {reviewed.Member}")
                .ToList();

            Assert.True(
                unused.Count == 0,
                "Remove these exceptions from HeadlessBans.ReviewedUses:" + Environment.NewLine + string.Join(Environment.NewLine, unused));
        }

        // Every rule fires in a headless namespace, the reviewed exception covers only its own caller, and code outside
        // the headless namespaces is not checked.
        [Fact]
        public void Scanner_ReportsEveryBanInHeadlessCodeOnly()
        {
            const string Fixture = "XMAT.Operations.ParityFixtures.HeadlessBanFixture::";
            IReadOnlyList<ForbiddenCall> uses = ForbiddenCallScanner.ScanHeadlessBans(typeof(HeadlessBanFixture).Assembly.Location);
            Dictionary<string, string> reported = uses
                .Where(use => use.CallerType == typeof(HeadlessBanFixture).FullName)
                .ToDictionary(use => use.Caller + "|" + use.Callee, use => HeadlessBans.Find(use).Id, StringComparer.Ordinal);

            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Fixture + "AppModelSingleton|XMAT.Models.CaptureAppModel::AppModel"] = "app-model",
                [Fixture + "ActiveDatabase|XMAT.SharedInterfaces.ICaptureAppModel::get_ActiveDatabase"] = "app-model",
                [Fixture + "SavedSettings|XMAT.Models.CaptureAppSettings::Serialize"] = "app-settings",
                [Fixture + "Preferences|XMAT.Models.AppPreferencesModel::.ctor"] = "app-settings",
                [Fixture + "StorageFolder|XMAT.PublicUtilities::get_StorageDirectoryPath"] = "global-storage",
                [Fixture + "ApplicationName|XMAT.PublicUtilities::get_AppName"] = "app-info",
                [Fixture + "InstallFolder|XMAT.PublicUtilities::get_InstallDirectoryPath"] = "app-info",
                [Fixture + "ApplicationLog|XMAT.PublicUtilities::AppLog"] = "global-logging",
                [Fixture + "LocalizedText|XMAT.Localization::GetLocalizedString"] = "localization",
                [Fixture + "LanguageLoading|XMAT.Localization::LoadLanguage"] = "localization",
            };

            Assert.Equal(expected.OrderBy(entry => entry.Key), reported.OrderBy(entry => entry.Key));
            Assert.Equal(
                HeadlessBans.Rules.Select(rule => rule.Id).OrderBy(id => id, StringComparer.Ordinal),
                expected.Values.Distinct().OrderBy(id => id, StringComparer.Ordinal));
            Assert.DoesNotContain(uses, use => HeadlessBans.IsReviewed(use));
            Assert.DoesNotContain(uses, use => use.CallerType == typeof(HeadlessBanControlFixture).FullName);
        }

        private static List<ForbiddenCall> ScanSources() =>
            ForbiddenCallRatchetTests.DefaultSources().SelectMany(ForbiddenCallScanner.ScanHeadlessBans).ToList();

        // Names the use, what the rule forbids and what to do instead.
        private static string Describe(ForbiddenCall use)
        {
            HeadlessBans.Rule rule = HeadlessBans.Find(use);
            string sites = use.Count == 1 ? "1 call site" : $"{use.Count} call sites";
            return $"  {use.Caller} uses {use.Callee} ({sites}), {rule.Uses}. Instead, {rule.Instead}.";
        }
    }
}
