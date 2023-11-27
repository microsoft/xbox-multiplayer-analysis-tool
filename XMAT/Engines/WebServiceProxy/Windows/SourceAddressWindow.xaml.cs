// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.WebServiceCapture.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Interaction logic for SourceAddressWindow.xaml
    /// </summary>
    public partial class SourceAddressWindow : Window
    {
        public SourceAddressWindow()
        {
            InitializeComponent();
        }

        internal PingableSourceAddressModel SelectedSourceAddress
        {
            get
            {
                foreach (PingableSourceAddressModel model in this.SourceAddressList.ItemsSource)
                {
                    if (model.IsSelected)
                    {
                        return model;
                    }
                }
                return null;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void PingDevice_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            PingableSourceAddressModel pingParameters = e.Parameter as PingableSourceAddressModel;
            e.CanExecute = pingParameters.CanPing;
        }

        private async void PingDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            PingableSourceAddressModel pingParameters = e.Parameter as PingableSourceAddressModel;
            pingParameters.CanPing = false;
            pingParameters.PingStatus = @"Pinging...";
            using (var bo = PublicUtilities.BlockingOperation())
            {
                await Task.Run(() =>
                {
                    string pingResult = string.Empty;
                    try
                    {
                        IntPtr? icmpHandle = InteropPublicUtilities.IcmpInterop.GetIcmpHandle();
                        InteropPublicUtilities.PingReply reply = InteropPublicUtilities.Send(
                            icmpHandle.Value,
                            pingParameters.SourceAddress,
                            pingParameters.DestinationAddress);
                        InteropPublicUtilities.IcmpInterop.ReleaseIcmpHandle(icmpHandle);
                        pingResult = reply.Status.ToString();
                    }
                    catch (Exception)
                    {
                        pingResult = @"failure (exception)";
                        // TODO: handle the exception gracefully and provide
                        // feedback
                    }
                    finally
                    {
                        PublicUtilities.SafeInvoke(() =>
                        {
                            pingParameters.PingStatus = pingResult;
                            pingParameters.CanPing = true;
                        });
                    }
                });
            }
        }
    }
}
