// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XMAT.NetworkTrace;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.DesignSystem
{
    // A state as the Status page lists it, with its meaning.
    public sealed record StateView(string State, StatusMeaning Meaning)
    {
        public string MeaningText => Meaning == StatusMeaning.Attention ? "Needs attention" : Meaning.ToString();
    }

    // The Status page: how each meaning shows, the meaning of every state XMAT shows, and the status classes' fills in
    // charts, all read from StatusMeanings.
    public partial class StatusStory : Story
    {
        public StatusStory()
        {
            InitializeComponent();
            StatesSection.Items.Add(Group("HTTP status, in a table",
                Enum.GetValues<TrafficStatusClass>().Select(status => new StateView(ClassName(status), StatusMeanings.Of(status)))));
            StatesSection.Items.Add(Group("Network trace",
                Enum.GetValues<DeviceStatusEnum>().Select(state => new StateView(state.ToString(), StatusMeanings.Of(state)))));
            StatesSection.Items.Add(Group("A device's proxy setting",
                Enum.GetValues<ProxyConfigurationState>().Select(state => new StateView(state.ToString(), StatusMeanings.Of(state)))));
            StatesSection.Items.Add(Group("XMAT's web proxy",
                Enum.GetValues<ProxyState>().Select(state => new StateView(state.ToString(), StatusMeanings.Of(state)))));
            foreach (TrafficStatusClass status in Enum.GetValues<TrafficStatusClass>())
            {
                ChartsSection.Items.Add(ChartExample(status));
            }
        }

        // The states the page lists, for the tests.
        internal IEnumerable<StateView> States =>
            StatesSection.Items.Cast<StoryExample>().SelectMany(example => ((ItemsControl)example.Content).Items.Cast<StateView>());

        // A status class as people read it, such as 4xx.
        internal static string ClassName(TrafficStatusClass status) => status switch
        {
            TrafficStatusClass.Success => "2xx",
            TrafficStatusClass.Redirect => "3xx",
            TrafficStatusClass.ClientError => "4xx",
            TrafficStatusClass.ServerError => "5xx",
            _ => "No status",
        };

        private StoryExample Group(string title, IEnumerable<StateView> states) => new()
        {
            Caption = title,
            Content = new ItemsControl
            {
                Focusable = false,
                ItemsSource = states.ToList(),
                ItemTemplate = (DataTemplate)Resources["StateTemplate"],
            },
        };

        private static StoryExample ChartExample(TrafficStatusClass status)
        {
            var swatch = new Border { Width = 40, Height = 16, CornerRadius = new CornerRadius(2) };
            swatch.SetResourceReference(Border.BackgroundProperty, StatusMeanings.ChartFill(status));
            return new StoryExample
            {
                Caption = StatusMeanings.ChartFill(status),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        swatch,
                        new TextBlock { Text = ClassName(status), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
        }
    }
}
