// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.SharedInterfaces;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT;
using XMAT.Models;

namespace NetworkTraceCaptureControls
{
    /// <summary>
    /// Interaction logic for CaptureConnectionView.xaml
    /// </summary>
    public partial class NetworkTraceCaptureView : UserControl
    {
        // Potential TODO: Move this to the CaptureController to save state between tabs?
        public NetworkTraceCaptureView()
        {
            InitializeComponent();

            AnalyzeCaptureButton.Content = XMAT.Localization.GetLocalizedString("NETCAP_ANALYZE");
            ClearCapturesButton.Content = XMAT.Localization.GetLocalizedString("NETCAP_CLEAR");
            PacketRawTextTab.Header = XMAT.Localization.GetLocalizedString("NETCAP_PACKET_DATA");
            FiltersText.Text = XMAT.Localization.GetLocalizedString("GENERIC_FILTERS");

            PidFilters.FilterDesc = XMAT.Localization.GetLocalizedString("NETCAP_FILTER_PROCESID");
            TidFilters.FilterDesc = XMAT.Localization.GetLocalizedString("NETCAP_FILTER_THREADID");
            ProtocolFilters.FilterDesc = XMAT.Localization.GetLocalizedString("NETCAP_FILTER_PROTOCOL");
            SourceIpFilters.FilterDesc = XMAT.Localization.GetLocalizedString("NETCAP_FILTER_SOURCEIP");
            DestIpFilters.FilterDesc = XMAT.Localization.GetLocalizedString("NETCAP_FILTER_DESTIP");

            PacketList.Columns[0].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_TIMESTAMP");
            PacketList.Columns[1].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_PROTOCOL");
            PacketList.Columns[2].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_STARTPACKET");
            PacketList.Columns[3].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_ENDPACKET");
            PacketList.Columns[4].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_FRAGMENT");
            PacketList.Columns[5].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_SEND");
            PacketList.Columns[6].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_RECEIVE");
            PacketList.Columns[7].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_LENGTH");
            PacketList.Columns[8].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_SOURCEIP");
            PacketList.Columns[9].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_DESTIP");
            PacketList.Columns[10].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_SOURCEMAC");
            PacketList.Columns[11].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_DESTMAP");
            PacketList.Columns[12].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_TYPE");
            PacketList.Columns[13].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_PROCESSID");
            PacketList.Columns[14].Header = XMAT.Localization.GetLocalizedString("NETCAP_HEADER_THREADID");
        }

        public static string EnabledText
        {
            get { return XMAT.Localization.GetLocalizedString("NETCAP_START"); }
        }

        public static string DisabledText
        {
            get { return XMAT.Localization.GetLocalizedString("NETCAP_STOP"); }
        }

        private async void StartStopCapture_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            var ntdcc = captureDeviceContext.CaptureController as NetworkTraceCaptureController;

            try
            {
                if (ntdcc.IsCapturing)
                {
                    await ntdcc.StopCapture(captureDeviceContext);
                }
                else
                {
                    ntdcc.ClearAllCaptures();
                    await ntdcc.StartCapture(captureDeviceContext);
                }
            }
            catch (UnauthorizedAccessException)
            {
                MessageBoxResult result = MessageBox.Show(
                    XMAT.Localization.GetLocalizedString("ELEVATION_REQUIRED_MESSAGE"),
                    XMAT.Localization.GetLocalizedString("ELEVATION_REQUIRED_TITLE"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.Yes);

                if (result == MessageBoxResult.Yes)
                {
                    PublicUtilities.RestartAsAdmin();
                }
                else
                {
                    await ntdcc.DisconnectFromDevice(captureDeviceContext);
                    ntdcc.Initialize();
                }
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message, XMAT.Localization.GetLocalizedString("NETCAP_ERROR"));
                await ntdcc.DisconnectFromDevice(captureDeviceContext);
                ntdcc.Initialize();
                CaptureAppModel.AppModel.StatusBarText1 = String.Empty;
            }
        }

        private void StartStopCapture_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null)
            {
                var ntdcc = captureDeviceContext.CaptureController as NetworkTraceCaptureController;

                if (captureDeviceContext.IsReadOnly)
                {
                    e.CanExecute = false;
                }
                else if (ntdcc.IsCapturing)
                {
                    e.CanExecute = (ntdcc.DeviceStatus == DeviceStatusEnum.Tracing);
                }
                else
                {
                    e.CanExecute = (ntdcc.DeviceStatus == DeviceStatusEnum.Idle);
                }
            }
        }

        public ObservableCollection<NetworkTracePacketDataModel> NetworkTracePackets { get; set; }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            var ntdcc = (this.DataContext as ICaptureDeviceContext).CaptureController as NetworkTraceCaptureController;

            (PacketList.Items as INotifyCollectionChanged).CollectionChanged += NetworkTraceCaptureView_CollectionChanged;
            ntdcc.PropertyChanged += Ntdcc_PropertyChanged;

            if (ntdcc.NetworkTracePackets.Count > 0)
            {
                BuildFilters();
                UpdateFilteredData();
            }
        }

        private void Ntdcc_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NetworkTraceCaptureController.NetworkTracePackets))
            {
                UpdateFilteredData();
            }
            else if (e.PropertyName == nameof(NetworkTraceCaptureController.IsCapturing))
            {
                var ntdcc = (this.DataContext as ICaptureDeviceContext).CaptureController as NetworkTraceCaptureController;

                if (ntdcc.IsCapturing == false)
                {
                    BuildFilters();
                    UpdateFilteredData();
                }
            }
        }

        private void NetworkTraceCaptureView_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null)
            {
                var ntdcc = captureDeviceContext.CaptureController as NetworkTraceCaptureController;

                if (ntdcc != null)
                {
                    ntdcc.FilteredItems = PacketList.Items;
                }
            }
        }

        private void OnFiltersChanged(object sender, RoutedEventArgs e)
        {
            UpdateFilteredData();
        }

        private bool ItemIsChecked(IEnumerable list, string text)
        {
            foreach(CheckedListItem item in list)
            {
                if (item.Text == text)
                {
                    return item.IsChecked;
                }
            }

            return true;
        }

        private void UpdateFilterList(IEnumerable items, ObservableCollection<CheckedListItem> list)
        {
            list.Clear();

            foreach (CheckedListItem item in items)
            {
                list.Add(item);
            }
        }

        private void BuildFilters()
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null)
            {
                var ntdcc = captureDeviceContext.CaptureController as NetworkTraceCaptureController;

                if (ntdcc != null && ntdcc.IsCapturing == false)
                {
                    var currentItems = ntdcc.PidFilterList.ToList();
                    var filterQuery =
                        from packet in ntdcc.NetworkTracePackets
                        orderby packet.ProcessId
                        group packet by packet.ProcessId into processGroup
                        select new CheckedListItem
                        {
                            Text = processGroup.Key.ToString(),
                            IsChecked = ItemIsChecked(currentItems, processGroup.Key.ToString())
                        };

                    UpdateFilterList(filterQuery, ntdcc.PidFilterList);

                    currentItems = ntdcc.TidFilterList.ToList();
                    filterQuery =
                        from packet in ntdcc.NetworkTracePackets
                        orderby packet.ThreadId
                        group packet by packet.ThreadId into threadGroup
                        select new CheckedListItem
                        {
                            Text = threadGroup.Key.ToString(),
                            IsChecked = ItemIsChecked(currentItems, threadGroup.Key.ToString())
                        };

                    UpdateFilterList(filterQuery, ntdcc.TidFilterList);

                    currentItems = ntdcc.ProtocolFilterList.ToList();
                    filterQuery =
                        from packet in ntdcc.NetworkTracePackets
                        orderby packet.Protocol
                        group packet by packet.Protocol into protoGroup
                        select new CheckedListItem
                        {
                            Text = GetProtocolFilterName(protoGroup.Key),
                            IsChecked = ItemIsChecked(currentItems, GetProtocolFilterName(protoGroup.Key))
                        };

                    UpdateFilterList(filterQuery, ntdcc.ProtocolFilterList);

                    currentItems = ntdcc.SourceIpFilterList.ToList();
                    filterQuery =
                        from packet in ntdcc.NetworkTracePackets
                        orderby packet.SourceIpv4Address
                        group packet by packet.SourceIpv4Address into sourceGroup
                        select new CheckedListItem
                        {
                            Text = sourceGroup.Key,
                            IsChecked = ItemIsChecked(currentItems, sourceGroup.Key)
                        };

                    UpdateFilterList(filterQuery, ntdcc.SourceIpFilterList);

                    currentItems = ntdcc.DestIpFilterList.ToList();
                    filterQuery =
                        from packet in ntdcc.NetworkTracePackets
                        orderby packet.DestinationIpv4Address
                        group packet by packet.DestinationIpv4Address into destGroup
                        select new CheckedListItem
                        {
                            Text = destGroup.Key,
                            IsChecked = ItemIsChecked(currentItems, destGroup.Key)
                        };

                    UpdateFilterList(filterQuery, ntdcc.DestIpFilterList);
                }
            }
        }

        private string GetProtocolFilterName(int protocolNum)
        {
            if (Enum.IsDefined(typeof(NetworkProtocol), protocolNum))
            {
                return $"{protocolNum} ({((NetworkProtocol)protocolNum).ToString()})";
            }

            return protocolNum.ToString();
        }

        private List<string> GetFilterNames(IEnumerable items)
        {
            List<string> list = new List<string>();

            foreach (CheckedListItem item in items)
            {
                if (item.IsChecked)
                {
                    list.Add(item.Text);
                }
            }

            return list;
        }

        private void UpdateFilteredData()
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null)
            {
                var ntdcc = captureDeviceContext.CaptureController as NetworkTraceCaptureController;

                if (ntdcc != null && ntdcc.IsCapturing == false)
                {
                    var filteredList = ntdcc.NetworkTracePackets as IEnumerable<NetworkTracePacketDataModel>;

                    if (PidFilters.IsFilterEnabled)
                    {
                        var filterNames = GetFilterNames(ntdcc.PidFilterList);

                        filteredList =
                            from packet in filteredList
                            where filterNames.Contains(packet.ProcessId.ToString())
                            select packet;
                    }

                    if (TidFilters.IsFilterEnabled)
                    {
                        var filterNames = GetFilterNames(ntdcc.TidFilterList);

                        filteredList =
                            from packet in filteredList
                            where filterNames.Contains(packet.ThreadId.ToString())
                            select packet;
                    }

                    if (ProtocolFilters.IsFilterEnabled)
                    {
                        var filterNames = GetFilterNames(ntdcc.ProtocolFilterList);

                        filteredList =
                            from packet in filteredList
                            where filterNames.Contains(GetProtocolFilterName(packet.Protocol))
                            select packet;
                    }

                    if (SourceIpFilters.IsFilterEnabled)
                    {
                        var filterNames = GetFilterNames(ntdcc.SourceIpFilterList);

                        filteredList =
                            from packet in filteredList
                            where filterNames.Contains(packet.SourceIpv4Address)
                            select packet;
                    }

                    if (DestIpFilters.IsFilterEnabled)
                    {
                        var filterNames = GetFilterNames(ntdcc.DestIpFilterList);

                        filteredList =
                            from packet in filteredList
                            where filterNames.Contains(packet.DestinationIpv4Address)
                            select packet;
                    }

                    NetworkTracePackets = new ObservableCollection<NetworkTracePacketDataModel>(filteredList);
                    PacketList.ItemsSource = NetworkTracePackets;
                }
            }
        }
    }
}
