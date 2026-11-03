// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT
{
    internal sealed class CaptureInvestigationReportWindow : Window
    {
        private static readonly Dictionary<(CaptureAppModel Model, Guid Report), CaptureInvestigationReportWindow> Windows = new();
        private readonly CaptureAppModel _model;
        private readonly CaptureDataResult _result;

        private CaptureInvestigationReportWindow(CaptureAppModel model, CaptureDataResult result, Window owner)
        {
            _model = model;
            _result = result;
            Owner = owner;
            Title = Localization.GetLocalizedString("DATA_REPORT_WINDOW_TITLE");
            Width = 1000;
            Height = 650;
            SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
            SetResourceReference(ForegroundProperty, "PrimaryForegroundBrush");
            var root = new DockPanel { Margin = new Thickness(12) };
            var provenance = new TextBlock
            {
                Text = Localization.GetLocalizedString("DATA_REPORT_WINDOW_PROVENANCE",
                    result.Report.Id, result.SourceId, result.SnapshotId, result.CapturedAt, result.MatchedRecords),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(provenance, Dock.Top);
            root.Children.Add(provenance);
            var tabs = new TabControl();
            Add("DATA_REPORT_WINDOW_FINDINGS", result.Findings);
            Add("DATA_REPORT_WINDOW_METRICS", result.Metrics);
            Add("DATA_REPORT_WINDOW_RECORDS", result.Records);
            Add("DATA_REPORT_WINDOW_DOCUMENTS", result.Report.Documents);
            root.Children.Add(tabs);
            Content = root;
            Closed += OnClosed;

            void Add(string key, IEnumerable rows)
            {
                var grid = new DataGrid
                {
                    IsReadOnly = true,
                    AutoGenerateColumns = true,
                    ItemsSource = rows,
                    EnableRowVirtualization = true,
                    EnableColumnVirtualization = true,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false
                };
                grid.SetResourceReference(BackgroundProperty, "PanelBackgroundBrush");
                grid.SetResourceReference(ForegroundProperty, "PrimaryForegroundBrush");
                tabs.Items.Add(new TabItem { Header = Localization.GetLocalizedString(key), Content = grid });
            }
        }

        internal static async Task<bool> ShowReportAsync(
            Window owner, CaptureAppModel model, DesktopTargetProvenance provenance, CancellationToken cancellationToken = default)
        {
            owner.Dispatcher.VerifyAccess();
            if (provenance?.ReportId is not Guid reportId || provenance.SnapshotId is not Guid snapshotId ||
                provenance.SourceId is not Guid sourceId)
                return false;
            if (Windows.TryGetValue((model, reportId), out var existing))
            {
                if (existing._result.SourceId != sourceId || existing._result.SnapshotId != snapshotId)
                    return false;
                existing.Activate();
                return true;
            }
            CaptureDataResult result;
            try
            { result = await model.RetainInvestigationReportAsync(reportId, snapshotId, cancellationToken); }
            catch (CaptureDataException) { return false; }
            if (result.SourceId != sourceId)
            {
                await result.DisposeAsync();
                return false;
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var window = new CaptureInvestigationReportWindow(model, result, owner);
                Windows.Add((model, reportId), window);
                window.Show();
                return true;
            }
            catch
            {
                await result.DisposeAsync();
                throw;
            }
        }

        internal static void CloseForModel(CaptureAppModel model)
        {
            foreach (var pair in new List<KeyValuePair<(CaptureAppModel Model, Guid Report), CaptureInvestigationReportWindow>>(Windows))
            {
                if (ReferenceEquals(pair.Key.Model, model))
                    pair.Value.Close();
            }
        }

        private void OnClosed(object sender, EventArgs args)
        {
            Windows.Remove((_model, _result.Report.Id));
            _model.TrackInvestigationCleanup(_result.DisposeAsync().AsTask());
        }
    }
}
