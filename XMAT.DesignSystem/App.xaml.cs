// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;

namespace XMAT.DesignSystem
{
    // The design system app. It opens on the new look in Light with Comfort view on; the settings above each page
    // preview the rest. The window holds the new look's resources itself, so the app has none of its own.
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // The pages show XMAT's controls with the labels XMAT gives them.
            Localization.LoadLanguage("en-us");
            MainWindow = new DesignSystemWindow("Light", comfortView: true);
            MainWindow.Show();
        }
    }
}
