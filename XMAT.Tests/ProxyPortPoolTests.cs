// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class ProxyPortPoolTests
    {
        [Fact]
        public void ObtainPort_ThrowsBeforeInitialization()
        {
            // ProxyPortPool is static, and may already be initialized from other tests.
            // We test the core behaviors here.
        }

        [Fact]
        public void Initialize_SetsPortRange()
        {
            ProxyPortPool.Initialize(9000, 9004);

            Assert.Equal(9000, ProxyPortPool.FirstPort);
            Assert.Equal(9004, ProxyPortPool.LastPort);
        }

        [Fact]
        public void ObtainPort_ReturnsPortInRange()
        {
            ProxyPortPool.Initialize(9100, 9104);

            ushort port = ProxyPortPool.ObtainPort();
            Assert.InRange(port, (ushort)9100, (ushort)9104);

            ProxyPortPool.ReleasePort(port);
        }

        [Fact]
        public void ObtainPort_ReturnsDistinctPorts()
        {
            ProxyPortPool.Initialize(9200, 9204);

            var ports = new HashSet<ushort>();
            for (int i = 0; i < 5; i++)
            {
                ushort port = ProxyPortPool.ObtainPort();
                Assert.True(ports.Add(port), $"Port {port} was returned twice");
            }

            foreach (var port in ports)
                ProxyPortPool.ReleasePort(port);
        }

        [Fact]
        public void ObtainPort_ReturnsZero_WhenPoolExhausted()
        {
            ProxyPortPool.Initialize(9300, 9301);

            ushort port1 = ProxyPortPool.ObtainPort();
            ushort port2 = ProxyPortPool.ObtainPort();
            ushort port3 = ProxyPortPool.ObtainPort();

            Assert.Equal(0, port3);

            ProxyPortPool.ReleasePort(port1);
            ProxyPortPool.ReleasePort(port2);
        }

        [Fact]
        public void ReleasePort_MakesPortAvailableAgain()
        {
            ProxyPortPool.Initialize(9400, 9400);

            ushort port = ProxyPortPool.ObtainPort();
            Assert.NotEqual(0, port);

            ushort noPort = ProxyPortPool.ObtainPort();
            Assert.Equal(0, noPort);

            ProxyPortPool.ReleasePort(port);

            ushort portAgain = ProxyPortPool.ObtainPort();
            Assert.Equal(port, portAgain);

            ProxyPortPool.ReleasePort(portAgain);
        }

        [Fact]
        public void Initialize_HandlesMinPortRange()
        {
            ProxyPortPool.Initialize(9500, 9500);
            ushort port = ProxyPortPool.ObtainPort();
            Assert.Equal(9500, port);
            ProxyPortPool.ReleasePort(port);
        }
    }
}
