// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class ProxyPortPoolTests
    {
        [Theory]
        [InlineData(9000, 9004)]
        [InlineData(65535, 65535)]
        [InlineData(65534, 65535)]
        [InlineData(1, 1)]
        public void ObtainPort_ReturnsEveryPortThenReportsExhaustion(int first, int last)
        {
            var pool = new ProxyPortPool();
            pool.Initialize((ushort)first, (ushort)last);
            var reservations = new List<ProxyPortPool.Reservation>();
            try
            {
                for (var port = first; port <= last; port++)
                {
                    var reservation = pool.ObtainPort($"owner-{port}");
                    reservations.Add(reservation);
                    Assert.Equal(port, reservation.Port);
                }
                Assert.Throws<InvalidOperationException>(() => pool.ObtainPort("exhausted"));
            }
            finally
            {
                reservations.ForEach(reservation => reservation.Dispose());
            }
            using var reused = pool.ObtainPort("new owner");
            Assert.Equal(first, reused.Port);
        }

        [Theory]
        [InlineData(9000, 9008)]
        [InlineData(9000, 9000)]
        [InlineData(9100, 9102)]
        public void RangeChange_PreservesOwnershipEvenOutsideNewRange(int first, int last)
        {
            var pool = new ProxyPortPool();
            pool.Initialize(9000, 9004);
            using var original = pool.ObtainPort("original");
            pool.Initialize((ushort)first, (ushort)last);
            Assert.Throws<InvalidOperationException>(() => pool.ReservePort(original.Port, "duplicate"));
            if (first != last)
            {
                using var next = pool.ObtainPort("new");
                Assert.NotEqual(original.Port, next.Port);
            }
        }

        [Fact]
        public void RestoredAndManualPorts_AreProtectedIndependentlyOfRange()
        {
            var pool = new ProxyPortPool();
            pool.Initialize(9000, 9002);
            using var restored = pool.ReservePort(9000, "restored");
            using var manual = pool.ReservePort(65535, "manual");
            using var next = pool.ObtainPort("new tab");
            Assert.Equal(9001, next.Port);
            Assert.Throws<InvalidOperationException>(() => pool.ReservePort(65535, "other"));
        }

        [Fact]
        public void StaleOrWrongToken_CannotReleaseANewerOwner()
        {
            var pool = new ProxyPortPool();
            var first = pool.ReservePort(9000, "first");
            new ProxyPortPool.Reservation(pool, 9000, "forged").Dispose();
            Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "duplicate"));
            first.Dispose();
            using var second = pool.ReservePort(9000, "second");
            first.Dispose();
            Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "third"));
        }

        [Theory]
        [InlineData(0, 9000)]
        [InlineData(9000, 8999)]
        public void InvalidRange_DoesNotChangeRangeOrOwnership(int first, int last)
        {
            var pool = new ProxyPortPool();
            using var current = pool.ReservePort(9000, "owner");
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Initialize((ushort)first, (ushort)last));
            Assert.Equal(8880, pool.FirstPort);
            Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "other"));
        }

        [Fact]
        public void Zero_IsNeverAReservation()
        {
            var pool = new ProxyPortPool();
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.ReservePort(0, "owner"));
        }

        [Fact]
        public void FullPortRange_DoesNotWrap()
        {
            var pool = new ProxyPortPool();
            pool.Initialize(1, 65535);
            using var port = pool.ObtainPort("first");
            Assert.Equal(1, port.Port);
            using var upper = pool.ReservePort(65535, "last");
            Assert.Equal(65535, upper.Port);
        }
    }
}
