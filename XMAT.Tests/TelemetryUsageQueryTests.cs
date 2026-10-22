// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;

namespace XMAT.Tests
{
    public class TelemetryUsageQueryTests
    {
        [Fact]
        public void AdoptionUsesLaunchCohortsRatherThanTheDayOfFeatureUse()
        {
            var query = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TelemetryUsage.kql"));
            Assert.Contains("summarize LaunchTime = min(Timestamp) by SessionId, AppVersion", query);
            Assert.Contains("extend Day = bin(LaunchTime, 1d)", query);
            Assert.Contains("join kind=inner launchSessions on SessionId, AppVersion", query);
            Assert.Contains("summarize FeatureSessions = dcount(SessionId) by Day, AppVersion, Feature", query);
            Assert.DoesNotContain("FeatureSessions = dcount(SessionId) by Day = bin(Timestamp", query);

            var yesterday = new DateTime(2026, 1, 1);
            var today = yesterday.AddDays(1);
            var launches = new[] { (Session: "A", Day: yesterday), (Session: "B", Day: yesterday), (Session: "C", Day: today) };
            var useAfterMidnight = new[] { "A", "B", "C", "UnobservedLaunch" };
            var cohorts = launches.GroupBy(x => x.Day).ToDictionary(x => x.Key, x => x.Count());
            var adoption = useAfterMidnight.Join(launches, session => session, launch => launch.Session,
                (_, launch) => launch).GroupBy(x => x.Day)
                .ToDictionary(group => group.Key, group => 100.0 * group.Count() / cohorts[group.Key]);
            Assert.Equal(100, adoption[yesterday]);
            Assert.Equal(100, adoption[today]);
        }

        [Theory]
        [InlineData("Imported", false, true)]
        [InlineData("User", true, false)]
        [InlineData("Restored", true, false)]
        [InlineData("Loaded", false, false)]
        public void MixedSessionFunnelsCreditOnlyTheOriginActuallyAnalyzed(string origin, bool captureConverted, bool importConverted)
        {
            var query = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TelemetryUsage.kql"));
            Assert.Contains("and CaptureOrigin in (\"User\", \"Restored\")", query);
            Assert.Contains("and CaptureOrigin == \"Imported\"", query);
            Assert.Contains("AnalyzedCaptureSessions = countif(CaptureWithData > 0 and AnalyzedCapture > 0)", query);
            Assert.Contains("AnalyzedImportSessions = countif(Imported > 0 and AnalyzedImport > 0)", query);
            var captureWithData = 1;
            var imported = 1;
            var analyzedCapture = origin is "User" or "Restored" ? 1 : 0;
            var analyzedImport = origin == "Imported" ? 1 : 0;
            Assert.Equal(captureConverted, captureWithData > 0 && analyzedCapture > 0);
            Assert.Equal(importConverted, imported > 0 && analyzedImport > 0);
        }
    }
}
