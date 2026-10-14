// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;

namespace XMAT
{
    internal static class ExceptionUtilities
    {
        internal static bool IsFatal(Exception exception)
        {
            if (exception is OutOfMemoryException and not InsufficientMemoryException or
                StackOverflowException or
                AccessViolationException or
                AppDomainUnloadedException or
                BadImageFormatException or
                CannotUnloadAppDomainException or
                InvalidProgramException)
            {
                return true;
            }

            if (exception is AggregateException aggregateException)
            {
                return aggregateException.InnerExceptions.Any(IsFatal);
            }

            return exception.InnerException != null && IsFatal(exception.InnerException);
        }
    }
}
