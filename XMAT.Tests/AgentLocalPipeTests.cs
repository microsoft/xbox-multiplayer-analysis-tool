// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using XMAT.AgentAccess;
using XMAT.AgentConnection;

namespace XMAT.Tests
{
    public class AgentLocalPipeTests
    {
        [Fact]
        public async Task NativeLocalOnlyPipeAcceptsTheMatchingUserAndElevationAndForwardsBytes()
        {
            var id = Guid.NewGuid();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var server = AgentLocalPipe.Create(id, 2);
            var accepting = server.WaitForConnectionAsync(timeout.Token);
            await using var client = new NamedPipeClientStream(".", $"XMAT.Agent.{id:N}", PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            await accepting;
            Assert.True(AgentLocalPipe.IsAllowedClient(server));
            await client.WriteAsync(new byte[] { 17, 29 }, timeout.Token);
            var bytes = new byte[2];
            await server.ReadExactlyAsync(bytes, timeout.Token);
            Assert.Equal(new byte[] { 17, 29 }, bytes);
            Assert.False(AgentProcessIdentity.MatchesCurrentUser(0));
        }
    }
}
