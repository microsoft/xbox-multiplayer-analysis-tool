// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureAppSettingsTests
    {
        // Only a hand-edited settings file can hold a device under the replay tab's name, which AddDeviceContext refuses. Such
        // entries are skipped and logged, without the name, and the other devices are restored in order; any other failure
        // to restore a device still stops the restore.
        [Fact]
        public void DeserializeTo_SkipsAndLogsReservedNames_RestoresTheOthers_AndLetsOtherFailuresThrough()
        {
            string[] reserved = { CaptureAppModel.ReplayContextName, CaptureAppModel.ReplayContextName + " (2)" };
            CaptureAppSettings.AppSettings settings = Settings("console-a", reserved[0], "Local PC", reserved[1]);
            var model = new CaptureAppModel();
            var restored = new List<string>();
            var logged = new List<string>();

            settings.DeserializeTo(model, device => restored.Add(device.DeviceName), logged.Add);

            Assert.Equal(new[] { "console-a", "Local PC" }, restored);
            Assert.Equal(2, logged.Count);
            Assert.All(logged, message => Assert.DoesNotContain(CaptureAppModel.ReplayContextName, message));
            Assert.All(reserved, name => Assert.Throws<ArgumentException>(() => model.AddDeviceContext(
                DeviceType.XboxConsole, name, CaptureType.WebProxy, false, null, deferPortAllocation: true)));
            Assert.Empty(model.CaptureDeviceContexts);

            var failure = new InvalidOperationException("Unrelated failure.");
            CaptureAppSettings.AppSettings unrelated = Settings(reserved[0], "console-b");
            Assert.Same(
                failure,
                Assert.Throws<InvalidOperationException>(() => unrelated.DeserializeTo(model, _ => throw failure, _ => { })));
        }

        // Saved devices as the settings file holds them, each a console's web proxy capture.
        private static CaptureAppSettings.AppSettings Settings(params string[] deviceNames) => new()
        {
            CaptureDevices = deviceNames
                .Select(name => new CaptureAppSettings.AppSettings.CaptureDevice
                {
                    DeviceType = DeviceType.XboxConsole,
                    DeviceName = name,
                    CaptureType = CaptureType.WebProxy,
                    IsSelected = true,
                })
                .ToList(),
        };
    }
}
