// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
using XMAT.Models;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for DisableDeviceOnCloseWindow.xaml
    /// </summary>
    public partial class DisableDeviceOnCloseWindow : Window
    {
        public DisableDeviceOnCloseWindow()
        {
            InitializeComponent();

            DisableDeviceOnCloseControl.Title = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_TITLE");
            OkButton.Content = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_YES");
            CancelButton.Content = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_NO");
            PromptPartOne.Text = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_PROMPT1");
            PromptPartTwo.Text = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_PROMPT2");
            PromptPartThree.Text = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_PROMPT3");
            AlwaysShow.Content = Localization.GetLocalizedString("PROXY_ERROR_DISABLE_ALWAYS");
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}
