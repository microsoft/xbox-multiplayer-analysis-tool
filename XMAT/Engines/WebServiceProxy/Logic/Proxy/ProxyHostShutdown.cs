// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed class ProxyHostDisposalException : Exception
    {
        internal ProxyHostDisposalException(
            Exception disposeException,
            Exception stopException = null) :
            base(
                "The proxy host could not be disposed.",
                stopException == null
                    ? disposeException
                    : new AggregateException(stopException, disposeException))
        {
        }
    }

    internal static class ProxyHostShutdown
    {
        internal static Task StopAndDisposeAsync(
            IHost host,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(host);

            // Host shutdown and disposal can block before returning a task.
            return Task.Run(() => StopAndDisposeCoreAsync(host, timeout));
        }

        private static async Task StopAndDisposeCoreAsync(
            IHost host,
            TimeSpan timeout)
        {
            Exception stopException = null;
            Exception disposeException = null;
            try
            {
                await host.StopAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                stopException = ex;
            }

            try
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    host.Dispose();
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                disposeException = ex;
            }

            if (stopException != null && disposeException != null)
            {
                throw new ProxyHostDisposalException(
                    disposeException,
                    stopException);
            }

            if (stopException != null)
            {
                ExceptionDispatchInfo.Capture(stopException).Throw();
            }

            if (disposeException != null)
            {
                throw new ProxyHostDisposalException(disposeException);
            }
        }
    }
}
