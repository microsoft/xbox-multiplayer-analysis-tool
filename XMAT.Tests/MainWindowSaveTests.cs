// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;

namespace XMAT.Tests
{
    public class MainWindowSaveTests
    {
        [Theory]
        [InlineData(
            "Refused",
            "CAPTURE_SAVE_ERROR_REFUSED",
            "The selected destination is a capture currently open in XMAT. Choose a different file.")]
        [InlineData(
            "DestinationLocked",
            "CAPTURE_SAVE_ERROR_LOCKED",
            "The capture could not be saved because another program is using the selected file.")]
        [InlineData(
            "Failed",
            "CAPTURE_SAVE_ERROR_FAILED",
            "The capture could not be saved.")]
        public void SaveFailure_UsesSpecificDataFreeLocalizedMessage(
            string failureName,
            string expectedKey,
            string expectedMessage)
        {
            Localization.LoadLanguage("en-us");
            CaptureSaveFailure failure =
                Enum.Parse<CaptureSaveFailure>(
                    failureName);

            string key =
                MainWindow.CaptureSaveFailureMessageKey(
                    failure);
            string message =
                Localization.GetLocalizedString(key);

            Assert.Equal(expectedKey, key);
            Assert.Equal(expectedMessage, message);
            Assert.DoesNotContain(
                SyntheticCanary,
                message,
                StringComparison.Ordinal);
            Assert.Equal(
                "Unable to Save Capture",
                Localization.GetLocalizedString(
                    "CAPTURE_SAVE_ERROR_TITLE"));
        }

        private const string SyntheticCanary =
            "XMATCANARY7f3c";
    }
}
