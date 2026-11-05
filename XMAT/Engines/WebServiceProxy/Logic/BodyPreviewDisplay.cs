// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using XMAT.Operations.Inspection;

namespace XMAT.WebServiceCapture
{
    // What the views show of a body preview apart from the body itself: the message bar, worded from the preview's
    // state and notices (never from the body), and the JSON tree.
    internal static class BodyPreviewDisplay
    {
        // The limits that the views preview with: the operation's defaults.
        private static readonly BodyPreviewLimits Limits = BodyPreviewLimits.Default;

        internal static string Pending => Localization.GetLocalizedString("BODY_PREVIEW_PENDING");

        internal static string Failed => Localization.GetLocalizedString("BODY_PREVIEW_FAILED");

        // The message bar of a preview: why nothing is shown when the body is empty (emptyKey says it for the view) or
        // was not kept, then one line for each notice. Null when there is nothing to say.
        internal static string Message(BodyPreview preview, string emptyKey)
        {
            ArgumentNullException.ThrowIfNull(preview);
            var lines = new List<string>();
            string state = preview.State switch
            {
                BodyPreviewState.Empty => Localization.GetLocalizedString(emptyKey),
                BodyPreviewState.NotRetained => Localization.GetLocalizedString("BODY_PREVIEW_NOT_RETAINED"),
                _ => null,
            };
            if (state != null)
            {
                lines.Add(state);
            }

            foreach (string code in preview.Notices ?? Array.Empty<string>())
            {
                lines.Add(Notice(code, preview));
            }

            return Join(lines);
        }

        // That the JSON tab has no tree although there is content: null when there is a tree, or when the message bar
        // already says why there is nothing (the body is empty or was not kept).
        internal static string NoJson(BodyPreview preview)
        {
            ArgumentNullException.ThrowIfNull(preview);
            return (preview.JsonDocuments?.Count ?? 0) > 0 ||
                preview.State is BodyPreviewState.Empty or BodyPreviewState.NotRetained
                ? null
                : Localization.GetLocalizedString("BODY_PREVIEW_NO_JSON");
        }

        // The message bar of a JSON tab that has no message bar above it: the preview's message, then NoJson.
        internal static string JsonMessage(BodyPreview preview, string message) =>
            Join(new[] { message, NoJson(preview) });

        // One notice, worded with the preview's counts and the limits; a code that this version does not know still
        // says that the preview may be incomplete.
        internal static string Notice(string code, BodyPreview preview) => code switch
        {
            BodyPreviewNotice.StoredPrefix => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_STORED_PREFIX", preview.StoredBytes),
            BodyPreviewNotice.InputLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_INPUT_LIMIT", preview.InspectedBytes, preview.StoredBytes),
            BodyPreviewNotice.DecodedLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_DECODED_LIMIT", preview.DecodedBytes),
            BodyPreviewNotice.TextLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_TEXT_LIMIT", Limits.MaxTextChars, Limits.MaxTextLines, Limits.MaxByteDumpBytes),
            BodyPreviewNotice.HexLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_HEX_LIMIT", Limits.MaxTextLines * 16L),
            BodyPreviewNotice.WorkLimit => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_WORK_LIMIT"),
            BodyPreviewNotice.HeaderLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_HEADER_LIMIT", Limits.MaxHeaderChars),
            BodyPreviewNotice.RawDeflate => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_RAW_DEFLATE"),
            BodyPreviewNotice.Incomplete => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_INCOMPLETE"),
            BodyPreviewNotice.UnverifiedEnd => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_UNVERIFIED_END"),
            BodyPreviewNotice.FramingIgnored => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_FRAMING_IGNORED"),
            BodyPreviewNotice.UnsupportedCoding =>
                Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_UNSUPPORTED_CODING"),
            BodyPreviewNotice.UnsupportedCharset =>
                Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_UNSUPPORTED_CHARSET"),
            BodyPreviewNotice.MalformedCoding =>
                Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_MALFORMED_CODING"),
            BodyPreviewNotice.MalformedText => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_MALFORMED_TEXT"),
            BodyPreviewNotice.Binary => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_BINARY"),
            BodyPreviewNotice.JsonLimit => Localization.GetLocalizedString(
                "BODY_PREVIEW_NOTICE_JSON_LIMIT", Limits.MaxJsonNodes, Limits.MaxJsonDepth, Limits.MaxJsonChars),
            BodyPreviewNotice.JsonIncomplete => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_JSON_INCOMPLETE"),
            BodyPreviewNotice.JsonMalformed => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_JSON_MALFORMED"),
            _ => Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_OTHER"),
        };

        // The JSON tab's tree, as the preview bounded it: one root for each document. Only the root of a single
        // document starts expanded; with several documents none does, and no other node ever does.
        internal static ObservableCollection<JsonTreeViewItem> Tree(IReadOnlyList<BodyPreviewNode> documents)
        {
            var roots = new ObservableCollection<JsonTreeViewItem>();
            foreach (BodyPreviewNode document in documents ?? Array.Empty<BodyPreviewNode>())
            {
                roots.Add(Item(document));
            }

            if (roots.Count == 1)
            {
                roots[0].AutoExpandJson = true;
            }

            return roots;
        }

        private static JsonTreeViewItem Item(BodyPreviewNode node)
        {
            var item = new JsonTreeViewItem { PropertyDescriptor = node.Label };
            foreach (BodyPreviewNode child in node.Children ?? Array.Empty<BodyPreviewNode>())
            {
                item.JsonTreeViewItems.Add(Item(child));
            }

            return item;
        }

        private static string Join(IEnumerable<string> lines)
        {
            string joined = string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrEmpty(line)));
            return joined.Length == 0 ? null : joined;
        }
    }
}
