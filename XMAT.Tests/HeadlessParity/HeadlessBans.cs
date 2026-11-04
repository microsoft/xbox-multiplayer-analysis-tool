// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.HeadlessParity
{
    // Members that the rest of the product may use but the headless namespaces (XMAT.Operations and XMAT.Cli) may not:
    // the GUI's app model and saved settings, PublicUtilities' application name, folders and log, and localized
    // strings. Headless code gets these from its caller instead; each rule says how. Only direct uses are checked.
    internal static class HeadlessBans
    {
        // Uses describes what the rule forbids; Instead is what headless code does instead.
        internal sealed record Rule(string Id, string Uses, string Instead, Func<string, string, bool> Matches);

        // A reviewed use that headless code still needs: the caller ("Type::Method"), the member ("Type::Member"), the
        // most call sites allowed and why. Adding one needs a reviewer's explicit approval (CONTRIBUTING.md).
        internal sealed record ReviewedUse(string Caller, string Member, int MaxCount, string Reason);

        internal static IReadOnlyList<Rule> Rules { get; } = new[]
        {
            new Rule(
                "app-model",
                "the GUI's app model (CaptureAppModel, including its AppModel singleton)",
                "take what the code needs from its request or OperationContext; only GUI adapters read the app model",
                (type, member) => IsType(type, "XMAT.Models.CaptureAppModel") ||
                    IsType(type, "XMAT.SharedInterfaces.ICaptureAppModel")),
            new Rule(
                "app-settings",
                "the GUI's saved settings (CaptureAppSettings, AppPreferencesModel)",
                "take the setting as a per-run option in the request, which xmat.exe binds from a flag; only the GUI " +
                    "reads saved preferences",
                (type, member) => IsType(type, "XMAT.Models.CaptureAppSettings") ||
                    IsType(type, "XMAT.Models.AppPreferencesModel")),
            new Rule(
                "global-storage",
                "global storage folders (PublicUtilities.StorageDirectoryPath, DesktopDirectoryPath, CollectLogs)",
                "write only under OperationContext.WorkingDirectory or a path that the request names",
                (type, member) => type == "XMAT.PublicUtilities" &&
                    (member is "get_StorageDirectoryPath" or "get_DesktopDirectoryPath" or "CollectLogs")),
            new Rule(
                "app-info",
                "PublicUtilities.AppName or InstallDirectoryPath, which run PublicUtilities' static initializer (it can " +
                    "open the GUI's application log); AppName also reads Application.Current",
                "use AppContext.BaseDirectory for the installation folder, and take any name to show from the caller " +
                    "(the request or OperationContext)",
                (type, member) => type == "XMAT.PublicUtilities" &&
                    (member is "get_AppName" or "get_InstallDirectoryPath")),
            new Rule(
                "global-logging",
                "the application log (PublicUtilities.AppLog)",
                "return an OperationDiagnostic (a stable code and a fixed English message) in the OperationResult, or " +
                    "report progress through OperationContext.Progress",
                (type, member) => type == "XMAT.PublicUtilities" && member == "AppLog"),
            new Rule(
                "localization",
                "localized strings (Localization)",
                "use stable codes with fixed English messages; GUI adapters localize what they show",
                (type, member) => IsType(type, "XMAT.Localization")),
        };

        // Explicitly typed, so the list still compiles when it holds no entry.
        internal static IReadOnlyList<ReviewedUse> ReviewedUses { get; } = new ReviewedUse[]
        {
            // Reviewed exception: xmat analyze bootstrap (begin)
            new ReviewedUse(
                "XMAT.Cli.CliAnalyzeCommand::AnalyzeAsync",
                "XMAT.Localization::LoadLanguage",
                1,
                "xmat analyze loads the English strings once, for the analysis rules that still look them up"),
            // Reviewed exception: xmat analyze bootstrap (end)
        };

        internal static Rule Find(string type, string member) =>
            Rules.FirstOrDefault(rule => rule.Matches(type, member));

        // The rule that a scanned use breaks; ForbiddenCall.Callee is "Type::Member".
        internal static Rule Find(ForbiddenCall use)
        {
            int separator = use.Callee.IndexOf("::", StringComparison.Ordinal);
            return Find(use.Callee.Substring(0, separator), use.Callee.Substring(separator + 2));
        }

        internal static bool IsReviewed(ForbiddenCall use) =>
            ReviewedUses.Any(reviewed =>
                reviewed.Caller == use.Caller && reviewed.Member == use.Callee && use.Count <= reviewed.MaxCount);

        // The type or one of its nested types.
        private static bool IsType(string type, string expected) =>
            type == expected || type.StartsWith(expected + "+", StringComparison.Ordinal);
    }
}
