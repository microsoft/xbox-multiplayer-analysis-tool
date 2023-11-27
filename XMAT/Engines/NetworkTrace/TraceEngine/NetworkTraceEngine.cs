// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;

namespace XMAT.NetworkTrace.NTDE
{
    public struct NetworkTraceEngineOptions
    {
        public string HostName { get; set; }
    }

    public static class NetworkTraceEngine
    {
        public static INetworkTraceEngine CreateLocal()
        {
            return new LocalNetworkTraceEngineImpl();
        }

        public static async Task<INetworkTraceEngine> Connect(NetworkTraceEngineOptions options)
        {
            return await NetworkTraceEngineImpl.Connect(options);
        }

        public static async Task Disconnect(INetworkTraceEngine engine)
        {
            await (engine as NetworkTraceEngineImpl).Disconnect();
        }
    }
}
