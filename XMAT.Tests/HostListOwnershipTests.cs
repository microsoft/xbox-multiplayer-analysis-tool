// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    // The Block and Bypass lists are one session policy owned by the application model,
    // injected into each capture before its proxy is initialized.
    public class HostListOwnershipTests
    {
        [Fact]
        public void ProductionContextFactory_InjectsOneAppPolicyBeforeEachProxyIsInitialized()
        {
            var app = new CaptureAppModel();
            var firstProxy = new WebServiceProxy();
            var first = new CaptureDeviceContextModel(
                DeviceType.GenericProxyDevice,
                "first-capture",
                CaptureType.WebProxy,
                false,
                null,
                deferPortAllocation: true,
                hostListPolicy: app.HostListPolicy,
                proxyFactory: () => firstProxy);

            // Edited after the first capture was initialized and before the second exists.
            Assert.True(app.HostListPolicy.BypassList.Add("bypass.example"));
            Assert.True(app.HostListPolicy.BlockList.Add("block.example"));

            var secondProxy = new WebServiceProxy();
            var second = new CaptureDeviceContextModel(
                DeviceType.GenericProxyDevice,
                "second-capture",
                CaptureType.WebProxy,
                false,
                null,
                deferPortAllocation: true,
                hostListPolicy: app.HostListPolicy,
                proxyFactory: () => secondProxy);

            var firstController = Assert.IsType<WebServiceDeviceCaptureController>(first.CaptureController);
            var secondController = Assert.IsType<WebServiceDeviceCaptureController>(second.CaptureController);
            Assert.Same(app.HostListPolicy, firstController.HostListPolicy);
            Assert.Same(app.HostListPolicy, secondController.HostListPolicy);
            Assert.Same(app.HostListPolicy.BlockList, firstController.BlockList);
            Assert.Same(app.HostListPolicy.BypassList, secondController.BypassList);
            Assert.Same(app.HostListPolicy.BypassList, firstProxy.BypassList);
            Assert.Same(app.HostListPolicy.BypassList, secondProxy.BypassList);
            Assert.True(firstProxy.BypassList.IsBypassed("bypass.example"));
            Assert.True(secondController.BlockList.IsBlocked("block.example"));
        }

        [Fact]
        public async Task ClosingACapture_KeepsTheSessionPolicyForExistingAndLaterCaptures()
        {
            var app = new CaptureAppModel();
            ICaptureDeviceContext imported = app.AddDeviceContext(
                DeviceType.Unknown, "imported-capture", CaptureType.WebProxy, true, null);
            var importedController = Assert.IsType<WebServiceDeviceCaptureController>(imported.CaptureController);
            Assert.Same(app.HostListPolicy.BlockList, importedController.BlockList);
            Assert.Same(app.HostListPolicy.BypassList, importedController.BypassList);
            Assert.True(importedController.BlockList.Add("kept.example"));

            await app.RemoveDeviceContextAsync(imported);

            Assert.True(app.HostListPolicy.BlockList.Contains("kept.example"));
            ICaptureDeviceContext later = app.AddDeviceContext(
                DeviceType.Unknown, "later-capture", CaptureType.WebProxy, true, null);
            var laterController = Assert.IsType<WebServiceDeviceCaptureController>(later.CaptureController);
            Assert.Same(app.HostListPolicy.BlockList, laterController.BlockList);
            Assert.True(laterController.BlockList.IsBlocked("kept.example"));
        }

        [Fact]
        public async Task LoadedCaptures_UseTheSessionPolicyAndKeepItWhenReloaded()
        {
            var app = new CaptureAppModel();
            string directory = Path.Combine(
                AppContext.BaseDirectory, "CaptureAppModelFixtures", Guid.NewGuid().ToString("N"));
            string path = SyntheticCaptures.WriteXmatcap(
                directory, "loaded-capture.xmatcap", new SyntheticXmatcapOptions(Devices: 2));
            IDisposable firstDatabase = null;
            try
            {
                ICaptureDeviceContext imported = app.AddDeviceContext(
                    DeviceType.Unknown, "imported-capture", CaptureType.WebProxy, true, null);

                app.LoadDataCaptures(path);
                firstDatabase = app.LoadedDatabase as IDisposable;
                ICaptureDeviceContext[] contexts = app.CaptureDeviceContexts.ToArray();
                Assert.Equal(3, contexts.Length);
                Assert.Same(imported, contexts[0]);
                Assert.All(contexts, context => Assert.True(context.IsReadOnly));
                Assert.All(contexts, context => Assert.Same(
                    app.HostListPolicy,
                    Assert.IsType<WebServiceDeviceCaptureController>(context.CaptureController).HostListPolicy));

                app.LoadDataCaptures(path);

                Assert.Equal(contexts, app.CaptureDeviceContexts);
                Assert.All(contexts, context => Assert.Same(
                    app.HostListPolicy,
                    Assert.IsType<WebServiceDeviceCaptureController>(context.CaptureController).HostListPolicy));
                var reloaded = Assert.IsType<WebServiceDeviceCaptureController>(contexts[2].CaptureController);
                Assert.True(reloaded.BlockList.Add("loaded.example"));
                Assert.True(Assert.IsType<WebServiceDeviceCaptureController>(imported.CaptureController)
                    .BlockList.IsBlocked("loaded.example"));
            }
            finally
            {
                while (app.CaptureDeviceContexts.Count != 0)
                {
                    await app.RemoveDeviceContextAsync(app.CaptureDeviceContexts[^1]);
                }
                firstDatabase?.Dispose();
                (app.LoadedDatabase as IDisposable)?.Dispose();
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void IndependentAppsAndStandaloneControllers_DoNotShareOrDefaultToStaticPolicy()
        {
            var first = new CaptureAppModel();
            var second = new CaptureAppModel();

            Assert.NotSame(first.HostListPolicy, second.HostListPolicy);
            Assert.NotSame(CaptureAppModel.AppModel.HostListPolicy, first.HostListPolicy);
            Assert.True(first.HostListPolicy.BlockList.Add("isolated.example"));
            Assert.False(second.HostListPolicy.BlockList.Contains("isolated.example"));
            Assert.False(CaptureAppModel.AppModel.HostListPolicy.BlockList.Contains("isolated.example"));

            var standaloneA = new WebServiceDeviceCaptureController(
                "standalone-a", DeviceType.GenericProxyDevice, false, new ProxyPortPool());
            var standaloneB = new WebServiceDeviceCaptureController(
                "standalone-b", DeviceType.GenericProxyDevice, false, new ProxyPortPool());
            Assert.NotSame(standaloneA.HostListPolicy, standaloneB.HostListPolicy);
            Assert.NotSame(CaptureAppModel.AppModel.HostListPolicy, standaloneA.HostListPolicy);
            Assert.True(standaloneA.BlockList.Add("standalone.example"));
            Assert.False(standaloneB.BlockList.Contains("standalone.example"));
        }

        [Fact]
        public void HostLists_AreNotPartOfTheApplicationSettingsSnapshot()
        {
            var app = new CaptureAppModel();
            var proxy = new WebServiceProxy();
            var capture = new CaptureDeviceContextModel(
                DeviceType.GenericProxyDevice,
                "settings-capture",
                CaptureType.WebProxy,
                false,
                null,
                deferPortAllocation: true,
                hostListPolicy: app.HostListPolicy,
                proxyFactory: () => proxy);
            app.CaptureDeviceContexts.Add(capture);
            Assert.True(app.HostListPolicy.BlockList.Add("session-only-block.example"));
            Assert.True(app.HostListPolicy.BypassList.Add("session-only-bypass.example"));

            string json = JsonSerializer.Serialize<object>(new CaptureAppSettings.AppSettings(app));

            Assert.Contains("settings-capture", json);
            Assert.DoesNotContain("session-only-block.example", json);
            Assert.DoesNotContain("session-only-bypass.example", json);
        }
    }
}
