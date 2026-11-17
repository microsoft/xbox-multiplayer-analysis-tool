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
    }
}
