// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using XMAT.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class MotionTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public MotionTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void SkeletonBlock_SweepsWhileShownAndStopsWhenHiddenRemovedOrAnimationEffectsTurnOff()
        {
            // High contrast shows the placeholder without a sweep.
            if (SystemParameters.HighContrast)
            {
                return;
            }

            _application.Run(window =>
            {
                Assert.Equal(SystemParameters.ClientAreaAnimation, MotionSettings.Current.AnimationsEnabled);
                window.Resources.MergedDictionaries.Add(Motion());
                // The application's resources hold the style; here the window's do, so the block takes it explicitly
                // to keep it while it is out of the window.
                var block = new SkeletonBlock { Width = 120, Style = (Style)window.FindResource(typeof(SkeletonBlock)) };
                var host = new StackPanel { Children = { block } };
                try
                {
                    MotionSettings.Current.Override = true;
                    WpfTestHost.Show(window, host);
                    var sheen = (FrameworkElement)block.Template.FindName("PART_Sheen", block);
                    var offset = Assert.IsType<TranslateTransform>(sheen.RenderTransform);
                    AssertSweeping(true);
                    Assert.Null(UIElementAutomationPeer.CreatePeerForElement(block));

                    // Hidden or removed, it stops rather than animating out of sight, and sweeps again when shown.
                    block.Visibility = Visibility.Collapsed;
                    AssertSweeping(false);
                    block.Visibility = Visibility.Visible;
                    WpfTestHost.Pump();
                    AssertSweeping(true);
                    host.Children.Remove(block);
                    WpfTestHost.Pump();
                    AssertSweeping(false);
                    host.Children.Add(block);
                    WpfTestHost.Pump();
                    AssertSweeping(true);

                    // Animation effects turned off mid-sweep leave the plain bar until the block is next shown.
                    MotionSettings.Current.Override = false;
                    AssertSweeping(false);
                    MotionSettings.Current.Override = true;
                    WpfTestHost.Pump();
                    AssertSweeping(false);

                    // Left alone, it sweeps twice and rests within 5 seconds.
                    block.Visibility = Visibility.Collapsed;
                    block.Visibility = Visibility.Visible;
                    WpfTestHost.Pump();
                    var clock = Stopwatch.StartNew();
                    AssertSweeping(true);
                    PumpUntil(() => !block.IsSweeping, TimeSpan.FromSeconds(8));
                    Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5.5));
                    AssertSweeping(false);

                    void AssertSweeping(bool sweeping)
                    {
                        Assert.Equal(sweeping, block.IsSweeping);
                        Assert.Equal(sweeping, offset.HasAnimatedProperties);
                        Assert.Equal(sweeping ? Visibility.Visible : Visibility.Collapsed, sheen.Visibility);
                    }
                }
                finally
                {
                    MotionSettings.Current.Override = null;
                }
            });
        }

        [Fact]
        public void AnalysisPanel_ShowsTheSkeletonOnlyForLongerAnalysesAndThenFadesTheResultsIn()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(Motion());
                var model = new CaptureAppModel();
                var view = new CaptureAnalysisControl();
                window.DataContext = model;
                var announced = new List<UIElement>();
                void Announced(UIElement element) => announced.Add(element);
                LiveRegion.Announced += Announced;
                try
                {
                    MotionSettings.Current.Override = true;
                    WpfTestHost.Show(window, view);
                    var tabs = (TabControl)view.FindName("CaptureAnalysisResultsTabs");
                    tabs.ContentTemplateSelector = null;
                    var run = new AnalysisRunModel(null, 1) { Header = "Results" };
                    model.AnalysisRuns.Add(run);
                    tabs.SelectedItem = run;
                    WpfTestHost.Pump();
                    var results = Named<ContentPresenter>(view, "AnalysisResults");
                    var skeleton = Named<Border>(view, "AnalysisSkeleton");
                    var label = Named<TextBlock>(view, "AnalysisSkeletonLabel");

                    // A quick analysis holds its results back and never shows the skeleton.
                    run.IsProcessing = true;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Hidden, results.Visibility);
                    Assert.False(skeleton.IsVisible);
                    run.IsProcessing = false;
                    WpfTestHost.Pump();
                    Assert.False(skeleton.IsVisible);
                    Assert.Equal(Visibility.Visible, results.Visibility);
                    Assert.True(results.HasAnimatedProperties, "The results fade in.");

                    // A longer one shows the skeleton after the delay, and screen readers hear its label once.
                    var clock = Stopwatch.StartNew();
                    run.IsProcessing = true;
                    PumpUntil(() => skeleton.IsVisible, TimeSpan.FromSeconds(5));
                    Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), $"Shown after {clock.Elapsed}");
                    var shown = Stopwatch.StartNew();
                    Assert.Equal(Visibility.Hidden, results.Visibility);
                    Assert.Equal(Localization.GetLocalizedString("ANALYSIS_LOADING"), label.Text);
                    var peer = Assert.Single(new FrameworkElementAutomationPeer(skeleton).GetChildren());
                    Assert.Same(label, Assert.IsAssignableFrom<UIElementAutomationPeer>(peer).Owner);
                    WpfTestHost.Pump();
                    Assert.Single(announced, element => element == label);

                    // Once processing ends, it stays up long enough not to flash, then the results fade in.
                    run.IsProcessing = false;
                    WpfTestHost.Pump();
                    Assert.True(skeleton.IsVisible);
                    PumpUntil(() => !skeleton.IsVisible, TimeSpan.FromSeconds(5));
                    Assert.True(shown.Elapsed >= TimeSpan.FromMilliseconds(300), $"Hidden after {shown.Elapsed}");
                    Assert.Equal(Visibility.Visible, results.Visibility);
                    Assert.True(results.HasAnimatedProperties, "The results fade in.");
                    PumpUntil(() => results.Opacity == 1, TimeSpan.FromSeconds(5));
                    Assert.Single(announced, element => element == label);

                    // The original look shows the run's own progress, as it always has.
                    LookState.Current.IsNewLook = false;
                    run.IsProcessing = true;
                    PumpFor(TimeSpan.FromMilliseconds(500));
                    Assert.Equal(Visibility.Visible, results.Visibility);
                    Assert.False(skeleton.IsVisible);
                    run.IsProcessing = false;
                }
                finally
                {
                    LookState.Current.IsNewLook = true;
                    MotionSettings.Current.Override = null;
                    LiveRegion.Announced -= Announced;
                    window.DataContext = null;
                }
            });
        }

        private static ResourceDictionary Motion() => new()
        {
            Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/Motion.xaml")
        };

        private static T Named<T>(DependencyObject parent, string name) where T : FrameworkElement =>
            Descendants(parent).OfType<T>().Single(element => element.Name == name);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < timeout, "The condition was not met in time.");
                WpfTestHost.Pump();
                Thread.Sleep(10);
            }
        }

        private static void PumpFor(TimeSpan duration)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration)
            {
                WpfTestHost.Pump();
                Thread.Sleep(10);
            }
        }
    }
}
