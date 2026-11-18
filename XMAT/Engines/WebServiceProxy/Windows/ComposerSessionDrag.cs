// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // A captured request that a connections list lends a composer for one drag, as Fiddler drags a session into its
    // composer: a copy of the row made when the drag began, so a live capture that updates the row afterwards changes
    // nothing, where it comes from, and the origin that a row of the Replays tab keeps (ReplayProvenance.Origin), read when
    // the drag began, which the composer restores. The copy shares the row's bodies and header dictionaries, which are never
    // changed in place (ProxyConnectionModel.RequestBody).
    internal sealed record ComposerSession(
        ProxyConnectionModel Capture, ReplayProvenance Provenance, Uri RecordedOrigin = null);

    // The drag's data holds an opaque id only, in a format of XMAT's own: the request, with its headers and body, stays in
    // this process and never enters data that another program could read. A composer takes the request at most once, and
    // only while the drag lasts; an id from another process, from a drag that has ended or of a request already taken
    // finds nothing. Text, links and files are not requests: they are refused, never parsed or read.
    internal static class ComposerSessionDrag
    {
        internal const string Format = "XMAT.ComposerSession.v1";

        private static readonly object Gate = new();
        private static readonly Dictionary<string, ComposerSession> Lent = new(StringComparer.Ordinal);

        // Lends a copy of row for one drag from the tab named sourceName; null when the row cannot be replayed (CONNECT,
        // a protocol upgrade, a request recorded without its body). Dispose the drag once it has ended.
        internal static ComposerSessionDragToken Begin(ProxyConnectionModel row, string sourceName)
        {
            if (!RequestReplayDraft.CanReplay(row))
            {
                return null;
            }

            var session = new ComposerSession(
                new ProxyConnectionModel(row),
                ReplayProvenance.Of(ReplaySourceKind.Edited, sourceName, row),
                row.Replay?.Origin);
            string id = Guid.NewGuid().ToString("N");
            lock (Gate)
            {
                Lent.Add(id, session);
            }

            var data = new DataObject();
            data.SetData(Format, id, false);
            return new ComposerSessionDragToken(id, data);
        }

        // Whether data is a drag that lends a request now; it stays lent.
        internal static bool IsLending(IDataObject data)
        {
            string id = IdOf(data);
            lock (Gate)
            {
                return id != null && Lent.ContainsKey(id);
            }
        }

        // The request that data lends, which no other drop can take afterwards; null for anything else.
        internal static ComposerSession Take(IDataObject data)
        {
            string id = IdOf(data);
            lock (Gate)
            {
                return id != null && Lent.Remove(id, out ComposerSession session) ? session : null;
            }
        }

        internal static void End(string id)
        {
            lock (Gate)
            {
                Lent.Remove(id);
            }
        }

        private static string IdOf(IDataObject data)
        {
            try
            {
                return data != null && data.GetDataPresent(Format, false) ? data.GetData(Format, false) as string : null;
            }
            catch (ExternalException)
            {
                // Data from another program that cannot be read is not a drag that XMAT began.
                return null;
            }
        }
    }

    // One drag of a lent request: its data, with the id only, and the end of the loan when disposed.
    internal sealed class ComposerSessionDragToken : IDisposable
    {
        internal ComposerSessionDragToken(string id, IDataObject data)
        {
            Id = id;
            Data = data;
        }

        internal string Id { get; }

        internal IDataObject Data { get; }

        public void Dispose() => ComposerSessionDrag.End(Id);
    }
}
