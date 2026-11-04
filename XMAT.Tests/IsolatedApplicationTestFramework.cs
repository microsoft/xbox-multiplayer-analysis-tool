// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("XMAT.Tests.IsolatedApplicationTestFramework", "XMAT.Tests")]

namespace XMAT.Tests
{
    public sealed class IsolatedApplicationTestFramework : XunitTestFramework
    {
        public IsolatedApplicationTestFramework(IMessageSink messageSink) : base(messageSink)
        {
            // PublicUtilities opens its log on first use, including in non-UI tests.
            // Establish the isolated application name before any tests can initialize it.
            _ = new CaptureUiApplicationFixture();
        }
    }
}
