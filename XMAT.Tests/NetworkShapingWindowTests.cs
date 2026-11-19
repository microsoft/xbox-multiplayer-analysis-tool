// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkShapingWindowTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PresetEditsRemainAvailableButTheRunningBodyDeadlineIsLocked(bool running)
        {
            application.Run(_ =>
            {
                var dialog = new NetworkShapingWindow(NetworkShapingProfile.Preset("min"), 1800, running);
                try
                {
                    var timeout = Assert.IsType<TextBox>(dialog.FindName("Timeout"));
                    Assert.Equal(!running, timeout.IsEnabled);
                    Assert.Equal("1800", timeout.Text);
                    var preset = Assert.IsType<ComboBox>(dialog.FindName("Preset"));
                    preset.SelectedItem = "avg";
                    Assert.Equal("1000", Assert.IsType<TextBox>(dialog.FindName("Down")).Text);
                    Assert.Equal("700", Assert.IsType<TextBox>(dialog.FindName("Up")).Text);
                    Assert.Equal("50", Assert.IsType<TextBox>(dialog.FindName("Latency")).Text);
                    preset.SelectedItem = "off";
                    Assert.Equal("0", Assert.IsType<TextBox>(dialog.FindName("Down")).Text);
                    Assert.Equal("0", Assert.IsType<TextBox>(dialog.FindName("Latency")).Text);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        [Fact]
        public void MatchingPresetIsSelectedAndNonShapingModesNeverRecordRates()
        {
            application.Run(_ =>
            {
                var dialog = new NetworkShapingWindow(NetworkShapingProfile.Preset("avg"), 120, running: false);
                try
                {
                    var preset = Assert.IsType<ComboBox>(dialog.FindName("Preset"));
                    var down = Assert.IsType<TextBox>(dialog.FindName("Down"));
                    Assert.Equal("avg", preset.SelectedItem);
                    Assert.True(down.IsEnabled);

                    preset.SelectedItem = "broken";
                    Assert.False(down.IsEnabled);
                    down.Text = "500";
                    Assert.True(dialog.TryReadSelection());
                    Assert.Equal(new NetworkShapingProfile(NetworkShapingMode.Broken), dialog.SelectedProfile);

                    preset.SelectedItem = "custom";
                    Assert.True(down.IsEnabled);
                    down.Text = "500";
                    Assert.True(dialog.TryReadSelection());
                    Assert.Equal(500, dialog.SelectedProfile.DownKbps);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        [Fact]
        public void EditedShapingValuesShowAsCustom()
        {
            application.Run(_ =>
            {
                var dialog = new NetworkShapingWindow(
                    new NetworkShapingProfile(NetworkShapingMode.Shape, DownKbps: 500), 120, running: false);
                try
                {
                    Assert.Equal("custom", Assert.IsType<ComboBox>(dialog.FindName("Preset")).SelectedItem);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }
    }
}
