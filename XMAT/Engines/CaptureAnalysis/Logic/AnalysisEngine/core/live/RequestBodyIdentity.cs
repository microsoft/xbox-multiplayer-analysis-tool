// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace CaptureAnalysisEngine
{
    // Canonical tokens intentionally use reference equality within one interner.
    internal sealed class RequestBodyIdentity
    {
        internal RequestBodyIdentity(
            long value)
        {
            Value = value;
        }

        internal long Value { get; }
    }

    internal sealed class RequestBodyIdentityInterner
    {
        private readonly Dictionary<
            string,
            RequestBodyIdentity> _identities;
        private long _nextIdentity;

        internal RequestBodyIdentityInterner(
            IEqualityComparer<string> comparer = null)
        {
            _identities =
                new Dictionary<
                    string,
                    RequestBodyIdentity>(
                        comparer ??
                        StringComparer.Ordinal);
        }

        internal int IdentityCount =>
            checked((int)_nextIdentity);

        internal int RepresentativeCount =>
            _identities.Count;

        internal RequestBodyIdentity GetOrAdd(
            string body)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (_identities.TryGetValue(
                    body,
                    out RequestBodyIdentity identity))
            {
                return identity;
            }

            identity =
                new RequestBodyIdentity(
                    checked(++_nextIdentity));
            // Keep the decoded string itself; do not copy request-body content.
            _identities.Add(
                body,
                identity);
            return identity;
        }

        internal RequestBodyIdentity CreateUnique()
        {
            return new RequestBodyIdentity(
                checked(++_nextIdentity));
        }

        internal void ClearRepresentatives()
        {
            _identities.Clear();
        }
    }

    internal sealed class RequestBodyIdentityResolver
    {
        private RequestBodyIdentityInterner _content;
        private Dictionary<
            ServiceCallItem,
            RequestBodyIdentity> _missing;

        internal RequestBodyIdentity Resolve(
            ServiceCallItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.BodyIdentity != null)
            {
                return item.BodyIdentity;
            }
            if (item.ReqBody != null)
            {
                _content ??=
                    new RequestBodyIdentityInterner();
                return _content.GetOrAdd(item.ReqBody);
            }
            _missing ??=
                new Dictionary<
                    ServiceCallItem,
                    RequestBodyIdentity>(
                        ReferenceEqualityComparer.Instance);
            if (!_missing.TryGetValue(
                    item,
                    out RequestBodyIdentity identity))
            {
                _content ??=
                    new RequestBodyIdentityInterner();
                identity =
                    _content.CreateUnique();
                _missing.Add(
                    item,
                    identity);
            }
            return identity;
        }
    }
}
