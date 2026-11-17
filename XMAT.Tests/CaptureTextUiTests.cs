// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Models;

namespace XMAT.Tests
{
    public class CaptureTextUiTests
    {
        [Theory]
        [InlineData(
            (int)CaptureTextSemantics.AmbiguousUnmarked,
            true,
            "CAPTURE_TEXT_AMBIGUOUS_WARNING")]
        [InlineData(
            (int)CaptureTextSemantics.AmbiguousUnmarked,
            false,
            null)]
        [InlineData(
            (int)CaptureTextSemantics.Literal,
            true,
            null)]
        public void CaptureOpenWarningKey_UsesTheOpenedFilesResult(
            int semantics,
            bool addedOrReplaced,
            string expected)
        {
            CaptureTextSource source =
                (CaptureTextSemantics)semantics ==
                    CaptureTextSemantics.Literal
                    ? CaptureTextSource.Literal
                    : CaptureTextSource.Unknown;

            Assert.Equal(
                expected,
                MainWindow.CaptureOpenWarningKey(
                    new CaptureLoadResult(
                        source,
                        addedOrReplaced)));
        }

        [Fact]
        public void FutureCaptureHasItsOwnOpenError()
        {
            Assert.Equal(
                "CAPTURE_OPEN_FUTURE_VERSION",
                MainWindow.CaptureOpenErrorKey(
                    SqLtSchemaFailure.UnsupportedVersion));
        }
    }
}
