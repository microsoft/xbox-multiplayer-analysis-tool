// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
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

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
            new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

        private sealed class Executor : XunitTestFrameworkExecutor
        {
            public Executor(
                AssemblyName assemblyName,
                ISourceInformationProvider sourceInformationProvider,
                IMessageSink diagnosticMessageSink)
                : base(assemblyName, sourceInformationProvider, diagnosticMessageSink)
            {
            }

            protected override async void RunTestCases(
                IEnumerable<IXunitTestCase> testCases,
                IMessageSink executionMessageSink,
                ITestFrameworkExecutionOptions executionOptions)
            {
                using var runner = new AssemblyRunner(
                    TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
                await runner.RunAsync();
            }
        }

        // Shuts the shared WPF application down and deletes its storage folder after the last test, before the runner
        // reports that the assembly finished. A ProcessExit handler is too late: vstest can end the test host 100 ms
        // after the session ends. A failure is reported as a test assembly cleanup failure.
        private sealed class AssemblyRunner : XunitTestAssemblyRunner
        {
            public AssemblyRunner(
                ITestAssembly testAssembly,
                IEnumerable<IXunitTestCase> testCases,
                IMessageSink diagnosticMessageSink,
                IMessageSink executionMessageSink,
                ITestFrameworkExecutionOptions executionOptions)
                : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
            {
            }

            protected override async Task BeforeTestAssemblyFinishedAsync()
            {
                await base.BeforeTestAssemblyFinishedAsync();
                await Aggregator.RunAsync(CaptureUiApplicationFixture.ShutDownHostAsync);
            }
        }
    }
}
