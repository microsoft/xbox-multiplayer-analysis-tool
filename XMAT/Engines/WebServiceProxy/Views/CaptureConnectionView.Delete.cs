// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Models;
using XMAT.Operations.Captures;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    public partial class CaptureConnectionView
    {
        // A delete item shows its key; on a read-only tab, where it is disabled, its tooltip points to export instead.
        private static MenuItem DeleteItem(string header, RoutedCommand command, ModifierKeys modifiers, IInputElement target, string readOnly)
        {
            MenuItem item = CommandItem(header, command, target);
            item.InputGestureText = new KeyGesture(Key.Delete, modifiers).GetDisplayStringForCulture(CultureInfo.CurrentUICulture);
            item.ToolTip = readOnly;
            ToolTipService.SetShowOnDisabled(item, true);
            return item;
        }

        // Delete deletes the visible selected rows and Shift+Delete the visible rows that are not selected; rows that filters
        // hide are never deleted. A read-only tab, opened or imported, offers export instead.
        private void DeleteSessions_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = TryGetCaptureContext(DataContext, out ICaptureDeviceContext tab, out var controller) &&
                CanDelete(tab, controller) && CaptureAppModel.AppModel.CanModifyCaptureData &&
                (e.Command == WebServiceProxyCommands.DeleteSelectedSessions
                    ? ConnectionList.SelectedItems.Count
                    : ConnectionList.Items.Count - ConnectionList.SelectedItems.Count) > 0;
            e.Handled = true;
        }

        // The capture this view shows, writable and neither starting nor stopping.
        private bool CanDelete(ICaptureDeviceContext tab, WebServiceDeviceCaptureController controller) =>
            ReferenceEquals(controller, _attachedController) && !tab.IsReadOnly && controller.CanDeleteSessions &&
            !controller.IsTransitioning;

        // The rows are frozen before the confirmation; after it, the original tab must still be shown and writable.
        private async void DeleteSessions_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TryGetCaptureContext(DataContext, out ICaptureDeviceContext tab, out var controller) ||
                (DeleteDialogs ?? Window.GetWindow(this) as ICaptureDeleteDialogService) is not { } dialogs ||
                FreezeDeletion(controller, e.Command == WebServiceProxyCommands.DeleteUnselectedSessions) is not { } plan ||
                !await dialogs.ConfirmDeleteAsync(plan.Prompt))
            {
                return;
            }

            string notice;
            try
            {
                notice = CaptureAppModel.AppModel.CaptureDeviceContexts.Contains(tab) &&
                    ReferenceEquals(tab.CaptureController, controller) && CanDelete(tab, controller)
                        ? Delete(controller, plan)
                        : Localization.GetLocalizedString("CAPTURE_DELETE_UNAVAILABLE");
            }
            finally
            {
                ReleaseMenus();
            }

            if (notice != null)
            {
                await dialogs.ShowDeleteNoticeAsync(notice);
            }
        }

        // Runs without waiting from the final filters to the restored selection; returns the notice to show, if any.
        private string Delete(WebServiceDeviceCaptureController controller, DeletePlan plan)
        {
            CaptureSourceStamp before = controller.SourceStamp;
            _deleting = true;
            try
            {
                ApplyPendingFilters();
                var visible = ConnectionList.Items.OfType<ProxyConnectionModel>().Select(row => row.RowKey).ToHashSet();
                if (!CaptureAppModel.AppModel.TryDeleteSessions(controller, plan.Targets, visible, out CaptureDeleteResult result))
                {
                    return Localization.GetLocalizedString("PROXY_OPERATION_BUSY");
                }

                RestoreAfterDeletion(controller, plan, result);
                return result.Deleted.Count == result.Requested
                    ? null
                    : Localization.GetLocalizedString(
                        "CAPTURE_DELETE_RETAINED", result.Deleted.Count, result.Requested, result.RetainedHidden, result.Unavailable);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Deleting sessions failed: {exception}");
                // A new stamp means the deletion committed: a view that failed afterwards never makes it "nothing deleted".
                return Localization.GetLocalizedString(
                    controller.SourceStamp == before ? "CAPTURE_DELETE_FAILED" : "CAPTURE_DELETE_VIEW_FAILED");
            }
            finally
            {
                _deleting = false;
            }
        }

        // What a delete command acts on, frozen when it runs: keys in the grid's order, weak targets and counts, never rows.
        private sealed record DeletePlan(
            CaptureDeletePrompt Prompt,
            IReadOnlyList<CaptureDeleteTarget> Targets,
            IReadOnlyList<CaptureRowKey> Order,
            IReadOnlyList<CaptureRowKey> Selected,
            CaptureRowKey Current);

        private DeletePlan FreezeDeletion(WebServiceDeviceCaptureController controller, bool unselected)
        {
            // Read before the pending filters apply: refreshing the grid's view collapses its selection to the current row.
            var selected = new HashSet<ProxyConnectionModel>(
                ConnectionList.SelectedItems.OfType<ProxyConnectionModel>(), ReferenceEqualityComparer.Instance);
            CaptureRowKey current = (ConnectionList.SelectedItem as ProxyConnectionModel)?.RowKey ?? default;
            ApplyPendingFilters();
            ProxyConnectionModel[] visible = ConnectionList.Items.OfType<ProxyConnectionModel>().ToArray();
            selected.IntersectWith(visible);
            ProxyConnectionModel[] victims = visible.Where(row => selected.Contains(row) != unselected).ToArray();
            if (victims.Length == 0)
            {
                return null;
            }

            int total = controller.ProxyConnections.Count;
            int incomplete = victims.Count(
                row => !row.HasRecordedResponseTimestamp || string.IsNullOrEmpty(row.ResponseLineAndHeaders));
            return new DeletePlan(
                new CaptureDeletePrompt(
                    unselected, victims.Length, total - victims.Length, selected.Count, total - visible.Length, incomplete),
                victims.Select(row => new CaptureDeleteTarget(row.RowKey, new WeakReference<ProxyConnectionModel>(row))).ToArray(),
                visible.Select(row => row.RowKey).ToArray(),
                visible.Where(selected.Contains).Select(row => row.RowKey).ToArray(),
                current);
        }

        // After the rows are removed (one Remove each for a few rows, else one Reset), Delete selects the next surviving
        // visible row in the frozen order, else the previous one, else none, and Shift+Delete keeps the surviving selection
        // and current row. Done once, on both paths: the grid's current row and cell, the details pane and the timeline
        // follow through the capture's selected row, and the restored row is scrolled into view.
        private void RestoreAfterDeletion(WebServiceDeviceCaptureController controller, DeletePlan plan, CaptureDeleteResult result)
        {
            var deleted = result.Deleted.ToHashSet();
            if (deleted.Count == 0)
            {
                return;
            }

            var shown = new HashSet<ProxyConnectionModel>(
                ConnectionList.Items.OfType<ProxyConnectionModel>(), ReferenceEqualityComparer.Instance);
            ProxyConnectionModel Shown(CaptureRowKey key) =>
                controller.ProxyConnections.ByKey(key) is { } row && shown.Contains(row) ? row : null;
            ProxyConnectionModel[] rows = plan.Prompt.Unselected
                ? plan.Selected.Select(Shown).Where(row => row != null).ToArray()
                : Array.Empty<ProxyConnectionModel>();
            int last = plan.Order.Select((key, index) => deleted.Contains(key) ? index : -1).Max();
            ProxyConnectionModel current = plan.Prompt.Unselected
                ? Shown(plan.Current) ?? rows.FirstOrDefault()
                : plan.Order.Skip(last + 1).Concat(plan.Order.Take(last).Reverse()).Select(Shown).FirstOrDefault(row => row != null);

            // The grid's selected item is bound to the capture's selected row.
            ConnectionList.SelectedItem = current;
            _connectionsView?.MoveCurrentTo(current);
            ConnectionList.CurrentItem = current;
            foreach (ProxyConnectionModel row in rows.Where(row => !ReferenceEquals(row, current)))
            {
                ConnectionList.SelectedItems.Add(row);
            }

            if (current != null)
            {
                ConnectionList.ScrollIntoView(current);
            }
        }
    }
}
