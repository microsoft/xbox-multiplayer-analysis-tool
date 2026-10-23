// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // Runs the real --proxy-guard process against a stand-in owner process and file-backed settings.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class ProxyGuardProcessTests : IDisposable
    {
        private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
        private readonly ProxyRecoveryTestEnvironment _environment = new();

        private static ProxySettings User => ProxyRecoveryTestEnvironment.UserSettings;

        private static ProxySettings Capturing => ProxyRecoveryTestEnvironment.CapturingSettings;

        private static ProxyEndpoint Endpoint => ProxyRecoveryTestEnvironment.Endpoint;

        public void Dispose() => _environment.Dispose();

        [Fact]
        public void KilledOwner_GuardRestoresTheSettingsFromBeforeCapture()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);
            Assert.Equal(Capturing, _environment.Store.Read());

            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KilledOwner_WithoutAReadableJournal_GuardRestoresFromItsLease(bool corrupt)
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);

            // Held so that the guard only recovers after the journal is gone.
            using (var gate = RecoveryLock.TryAcquire(_environment.JournalPath, GuardTimeout))
            {
                Assert.NotNull(gate);
                owner.Kill();
                owner.WaitForExit();
                BreakJournal(corrupt);
            }

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void NormalShutdown_RestoresTheSettingsAndDisarmsTheGuard()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);

            Assert.Equal(RecoveryResult.Restored, recovery.RestoreOnShutdown());

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // Disable is paused after it releases the lock while a new capture starts; when it resumes, it must
        // not disarm the new capture's guard.
        [Fact]
        public void DisableResumingAfterANewCapture_LeavesTheNewGuardInCharge()
        {
            var owner = _environment.StartOwner();
            var oldGuard = StartCapture(owner, out var recovery);
            Process newGuard = null;
            recovery.DisableUnlocked = () =>
            {
                recovery.Enable(Endpoint);
                newGuard = _environment.Guard(recovery);
            };

            recovery.Disable();

            Assert.NotNull(newGuard);
            Assert.True(oldGuard.WaitForExit(GuardTimeout));
            Assert.Equal(newGuard.Id, recovery.GuardProcessId);
            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));

            owner.Kill();

            Assert.True(newGuard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, newGuard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void FatalErrorRecovery_RestoresBeforeTheOwnerEnds()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out var recovery);

            Assert.Equal(RecoveryResult.Restored, recovery.TryRecoverAfterFatalError());
            Assert.Equal(User, _environment.Store.Read());
            var later = User with { Bypass = "changed after recovery" };
            _environment.Store.Write(later);
            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(later, _environment.Store.Read());
        }

        [Fact]
        public void SettingsChangedDuringCapture_AreNotOverwritten()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);
            var changed = User with { Flags = ProxySettings.Proxy, Server = "user-proxy:3128" };
            _environment.Store.Write(changed);

            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(changed, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public async Task UnexpectedGuardExit_RestoresTheSettingsAndReportsIt()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);

            guard.Kill();

            Assert.Equal(RecoveryResult.Restored, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void UnexpectedGuardExit_WithoutSubscribers_StillRestores()
        {
            var guard = StartCapture(_environment.StartOwner(), out _);

            guard.Kill();

            // The journal is removed only after the settings have been written.
            Assert.True(SpinWait.SpinUntil(() => !File.Exists(_environment.JournalPath), GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnexpectedGuardExit_WithoutAReadableJournal_RestoresFromTheLeaseInMemory(bool corrupt)
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            BreakJournal(corrupt);

            guard.Kill();

            Assert.Equal(RecoveryResult.Restored, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public async Task UnexpectedGuardExit_WithoutAReadableJournal_LeavesSettingsTheUserChanged()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            BreakJournal(corrupt: false);
            var changed = User with { Flags = ProxySettings.Proxy, Server = "user-proxy:3128" };
            _environment.Store.Write(changed);

            guard.Kill();

            Assert.Equal(RecoveryResult.SettingsChanged, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(changed, _environment.Store.Read());
        }

        [Fact]
        public async Task UnexpectedGuardExit_LeavesAReplacementLeaseAndItsSettingsAlone()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            var replacement = LeaseJournal.TryRead(_environment.JournalPath) with { Nonce = Guid.NewGuid().ToString("N") };
            LeaseJournal.Write(_environment.JournalPath, replacement);

            guard.Kill();

            Assert.Equal(RecoveryResult.SettingsChanged, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.Equal(replacement, LeaseJournal.TryRead(_environment.JournalPath));
        }

        [Fact]
        public void GuardThatCannotStart_LeavesTheProxyUnchanged()
        {
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                Path.Combine(_environment.Folder, "missing.exe"));

            Assert.Throws<Win32Exception>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void ReusedOwnerProcessId_GuardNeverBecomesReady()
        {
            var live = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            var recovery = _environment.Recovery(live with { CreationTime = live.CreationTime + 1 });

            Assert.Throws<InvalidOperationException>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void FailedProxyWrite_RemovesTheLeaseAndKeepsTheSettings()
        {
            var recovery = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));
            File.SetAttributes(_environment.Store.FilePath, FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsAny<UnauthorizedAccessException>(() => recovery.Enable(Endpoint));
            }
            finally
            {
                File.SetAttributes(_environment.Store.FilePath, FileAttributes.Normal);
            }

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        [Fact]
        public void ProxyWriteThatFailsAfterWritingEverything_IsRolledBack()
        {
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new FailingStore(_environment.Store.FilePath, requested => requested));

            Assert.Throws<IOException>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        // Settings that are neither the previous nor the requested ones are not taken over just to roll
        // them back. The lease keeps the settings from before capture, so a retry still restores those.
        [Fact]
        public void ProxyWriteThatFailsHalfway_KeepsTheSettingsAndTheLease()
        {
            var partial = User with { Server = Endpoint.ToString() };
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new FailingStore(_environment.Store.FilePath, _ => partial));

            Assert.Throws<IOException>(() => recovery.Enable(Endpoint));

            Assert.Equal(partial, _environment.Store.Read());
            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            Assert.Equal(User, lease.Prior);
            Assert.Null(lease.Applied);

            recovery.Enable(Endpoint);
            recovery.Disable();
            Assert.Equal(User, _environment.Store.Read());
        }

        [Fact]
        public void SecondInstance_CannotTakeOverALiveCapture()
        {
            StartCapture(_environment.StartOwner(), out _);
            var second = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));

            Assert.Throws<InvalidOperationException>(() => second.Enable(new ProxyEndpoint("127.0.0.1", 8881)));
            Assert.Throws<InvalidOperationException>(second.Disable);
            Assert.Null(second.HealAtStartup());

            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void GuardProcess_RunsWithoutStartingTheApplication()
        {
            var guard = StartCapture(_environment.StartOwner(), out _);
            guard.Refresh();

            var modules = guard.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).ToArray();

            Assert.DoesNotContain(modules, name =>
                name.StartsWith("PresentationNative", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("wpfgfx", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("e_sqlite3", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IntPtr.Zero, guard.MainWindowHandle);
        }

        // The guard is started in a job made here, so its own job check runs the same way wherever the
        // tests run.
        [Theory]
        [InlineData(true, ProxyGuard.JobRefused)]
        [InlineData(false, ProxyGuard.Completed)]
        public void IndependentGuard_ReportsReadyOnlyInAJobThatLetsItOutliveXmat(bool killOnClose, int exitCode)
        {
            var owner = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            var lease = _environment.WriteLease(owner, User, applied: false);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Ready(lease.Nonce));
            using var disarm = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Disarm(lease.Nonce));
            using var job = new TestJob(killOnClose);
            var guard = _environment.Track(job.Start(
                ProxyRecoveryTestEnvironment.GuardExecutable,
                ProxyGuard.Arguments(_environment.JournalPath, lease.Nonce, true, _environment.Store.FilePath)));
            using var exited = new ProcessExitHandle(guard.SafeHandle);

            WaitHandle.WaitAny(new WaitHandle[] { ready, exited }, GuardTimeout);
            disarm.Set();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(exitCode, guard.ExitCode);
            Assert.Equal(!killOnClose, ready.WaitOne(0));
        }

        private Process StartCapture(Process owner, out PcProxyRecovery recovery)
        {
            recovery = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(owner));
            recovery.Enable(Endpoint);
            return _environment.Guard(recovery);
        }

        private void BreakJournal(bool corrupt)
        {
            if (corrupt)
            {
                File.WriteAllText(_environment.JournalPath, "{");
            }
            else
            {
                File.Delete(_environment.JournalPath);
            }
        }

        private static Task<RecoveryResult> GuardLost(PcProxyRecovery recovery)
        {
            var lost = new TaskCompletionSource<RecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            recovery.GuardLost += result => lost.TrySetResult(result);
            return lost.Task;
        }

        // Fails the first write that turns the proxy on, after writing what `leftBehind` returns.
        private sealed class FailingStore : FileProxySettingsStore
        {
            private readonly Func<ProxySettings, ProxySettings> _leftBehind;
            private bool _failed;

            internal FailingStore(string path, Func<ProxySettings, ProxySettings> leftBehind)
                : base(path) => _leftBehind = leftBehind;

            public override void Write(ProxySettings settings)
            {
                if (_failed || !settings.UsesProxy)
                {
                    base.Write(settings);
                    return;
                }
                _failed = true;
                base.Write(_leftBehind(settings));
                throw new IOException("Simulated failure while writing the Windows proxy settings.");
            }
        }

        private sealed class TestJob : IDisposable
        {
            private const uint CreateSuspended = 0x4;
            private readonly SafeWaitHandle _job;

            internal TestJob(bool killOnClose)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var limits = new RecoveryNativeMethods.JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = killOnClose ? RecoveryNativeMethods.JobKillOnClose : 0;
                if (_job.IsInvalid ||
                    !SetInformationJobObject(
                        _job, RecoveryNativeMethods.JobExtendedLimitInformationClass, ref limits, Marshal.SizeOf(limits)))
                {
                    var error = new Win32Exception();
                    _job.Dispose();
                    throw error;
                }
            }

            // The process starts suspended, so it runs nothing before it is in the job.
            internal Process Start(string executable, IEnumerable<string> arguments)
            {
                var commandLine = string.Join(" ", arguments.Prepend(executable).Select(argument => $"\"{argument}\""));
                var startup = new RecoveryNativeMethods.StartupInfo { Size = Marshal.SizeOf<RecoveryNativeMethods.StartupInfo>() };
                if (!RecoveryNativeMethods.CreateProcess(
                        executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, CreateSuspended,
                        IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var information))
                {
                    throw new Win32Exception();
                }
                using var process = new SafeProcessHandle(information.Process, true);
                try
                {
                    if (!AssignProcessToJobObject(_job, process))
                    {
                        throw new Win32Exception();
                    }
                    var started = Process.GetProcessById(information.ProcessId);
                    _ = started.SafeHandle;
                    if (ResumeThread(information.Thread) == uint.MaxValue)
                    {
                        throw new Win32Exception();
                    }
                    return started;
                }
                catch
                {
                    RecoveryNativeMethods.TerminateProcess(process, 1);
                    throw;
                }
                finally
                {
                    RecoveryNativeMethods.CloseHandle(information.Thread);
                }
            }

            public void Dispose() => _job.Dispose();

            [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern SafeWaitHandle CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetInformationJobObject(
                SafeWaitHandle job, int informationClass,
                ref RecoveryNativeMethods.JobExtendedLimitInformation information, int length);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint ResumeThread(IntPtr thread);
        }
    }
}
