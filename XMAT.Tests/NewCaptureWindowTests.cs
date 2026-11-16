// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    // New Capture in the new look (Windows/NewCaptureWindow.xaml), and the choice cards it is made of.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NewCaptureWindowTests
    {
        private const string DefaultConsoleAddress = "192.168.1.30";

        private readonly CaptureUiApplicationFixture _application;

        public NewCaptureWindowTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void Opens_OnThisPcAndWebProxy_WithWhatEachCaptureTypeNeedsOnThisPc()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                Assert.Equal(NewCaptureWindow.Device.ThisPc, window.SelectedDevice);
                Assert.True(window.WebProxyCard.IsChecked);
                Assert.True(window.NetworkTraceCard.IsEnabled);
                Assert.True(window.AddButton.IsEnabled);
                Assert.Equal(Text("NEW_CAPTURE_NOTE_PC_PROXY"), window.WebProxyCard.Note);
                Assert.Equal(Text("NEW_CAPTURE_NOTE_PC_TRACE"), window.NetworkTraceCard.Note);
                Assert.NotNull(window.NetworkTraceCard.NoteIcon);
                Assert.False(window.AddressBox.IsVisible);
            }));
        }

        [Fact]
        public void NetworkTrace_IsUnavailableForGenericDevicesAndHandhelds_AndTheChoiceMovesToWebProxy()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                window.NetworkTraceCard.IsChecked = true;
                window.GenericDeviceCard.IsChecked = true;
                Assert.False(window.NetworkTraceCard.IsEnabled);
                Assert.True(window.WebProxyCard.IsChecked);
                Assert.Equal(Text("NEW_CAPTURE_TRACE_UNAVAILABLE_GENERIC"), window.NetworkTraceCard.Note);
                Assert.Equal(Text("NEW_CAPTURE_NOTE_MANUAL_PROXY"), window.WebProxyCard.Note);

                window.HandheldCard.IsChecked = true;
                Assert.False(window.NetworkTraceCard.IsEnabled);
                Assert.Equal(Text("NEW_CAPTURE_TRACE_UNAVAILABLE_HANDHELD"), window.NetworkTraceCard.Note);

                // Network Trace is available again on a console; the choice stays where it moved.
                window.DefaultConsoleCard.IsChecked = true;
                Assert.True(window.NetworkTraceCard.IsEnabled);
                Assert.True(window.WebProxyCard.IsChecked);
                Assert.Equal(Text("NEW_CAPTURE_NOTE_CONSOLE_PROXY"), window.WebProxyCard.Note);
                Assert.Equal(Text("NEW_CAPTURE_NOTE_CONSOLE_TRACE"), window.NetworkTraceCard.Note);
            }));
        }

        [Fact]
        public void WithoutTheGdk_TheConsolesAreUnavailable_AndTheArrowKeysPassThem()
        {
            _application.Run(_ => WithWindow(gdkInstalled: false, window =>
            {
                foreach (ChoiceCard console in new[] { window.DefaultConsoleCard, window.ConsoleByAddressCard })
                {
                    Assert.False(console.IsEnabled);
                    Assert.Equal(Text("NEW_CAPTURE_CONSOLE_UNAVAILABLE"), console.Note);
                    Assert.NotNull(console.NoteIcon);
                }

                Press(window.ThisPcCard, Key.Right);
                Assert.True(window.GenericDeviceCard.IsChecked);
                Press(window.GenericDeviceCard, Key.Left);
                Assert.True(window.ThisPcCard.IsChecked);
            }));
        }

        // The arrow keys choose the next or previous card of the same choice, in layout order and wrapping around;
        // Tab reaches only the chosen card of each choice.
        [Fact]
        public void ArrowKeys_ChooseWithinTheirChoice_AndOnlyTheChosenCardIsATabStop()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                ChoiceCard[] devices =
                {
                    window.ThisPcCard, window.DefaultConsoleCard, window.ConsoleByAddressCard, window.GenericDeviceCard,
                    window.HandheldCard,
                };
                Assert.Equal(devices, window.ThisPcCard.Group());
                AssertTabStops(window, window.ThisPcCard, window.WebProxyCard);

                Press(window.ThisPcCard, Key.Right);
                Assert.True(window.DefaultConsoleCard.IsChecked);
                Press(window.DefaultConsoleCard, Key.Down);
                Assert.True(window.ConsoleByAddressCard.IsChecked);
                Press(window.ConsoleByAddressCard, Key.Right);
                Press(window.GenericDeviceCard, Key.Right);
                Assert.True(window.HandheldCard.IsChecked);
                Press(window.HandheldCard, Key.Right);
                Assert.True(window.ThisPcCard.IsChecked);
                Press(window.ThisPcCard, Key.Up);
                Assert.True(window.HandheldCard.IsChecked);
                AssertTabStops(window, window.HandheldCard, window.WebProxyCard);

                // The capture types are a choice of their own; on a handheld, Network Trace isn't available.
                Press(window.WebProxyCard, Key.Right);
                Assert.True(window.WebProxyCard.IsChecked);
                Assert.True(window.HandheldCard.IsChecked);
                Press(window.HandheldCard, Key.Left);
                Press(window.GenericDeviceCard, Key.Left);
                Assert.True(window.ConsoleByAddressCard.IsChecked);
                Press(window.WebProxyCard, Key.Right);
                Assert.True(window.NetworkTraceCard.IsChecked);
                Assert.True(window.ConsoleByAddressCard.IsChecked);
                AssertTabStops(window, window.ConsoleByAddressCard, window.NetworkTraceCard);
            }));
        }

        [Fact]
        public void TheAddressField_ShowsWithItsCard_ChecksTheAddress_AndShowsAnErrorAfterAPauseInTyping()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                window.ConsoleByAddressCard.IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(window.AddressBox.IsVisible);
                Assert.False(window.AddButton.IsEnabled);
                Assert.Equal(Visibility.Visible, window.AddressExample.Visibility);
                Assert.Equal(Text("NEW_CAPTURE_ADDRESS_EXAMPLE"), AutomationProperties.GetHelpText(window.AddressBox));

                window.AddressBox.Text = "devkit 07";
                Assert.False(window.AddButton.IsEnabled);
                Assert.Equal(string.Empty, window.AddressError.Text);
                Assert.Equal(Visibility.Collapsed, window.AddressErrorIcon.Visibility);
                Assert.True(window.AddressPauseTimer.IsEnabled);

                window.AddressPauseTimer.Interval = TimeSpan.FromMilliseconds(1);
                WaitFor(() => window.AddressError.Text.Length > 0);
                Assert.Equal(Text("NEW_CAPTURE_ADDRESS_ERROR"), window.AddressError.Text);
                Assert.Equal(Visibility.Visible, window.AddressErrorIcon.Visibility);
                Assert.Equal(Visibility.Visible, window.AddressErrorOutline.Visibility);
                Assert.Equal(window.AddressError.Text, AutomationProperties.GetHelpText(window.AddressBox));

                // The error stays while the address isn't valid, and goes once it is.
                window.AddressBox.Text = "devkit 0";
                Assert.Equal(Text("NEW_CAPTURE_ADDRESS_ERROR"), window.AddressError.Text);
                window.AddressBox.Text = "192.168.1.20";
                Assert.True(window.AddButton.IsEnabled);
                Assert.Equal(string.Empty, window.AddressError.Text);
                Assert.Equal(Visibility.Collapsed, window.AddressErrorIcon.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AddressErrorOutline.Visibility);
                Assert.Equal(Visibility.Visible, window.AddressValidIcon.Visibility);

                window.AddressBox.Text = string.Empty;
                Assert.False(window.AddButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.AddressValidIcon.Visibility);
                Assert.Equal(Visibility.Visible, window.AddressExample.Visibility);

                // Other devices need no address.
                window.ThisPcCard.IsChecked = true;
                Assert.True(window.AddButton.IsEnabled);
            }));
        }

        [Fact]
        public void LeavingTheAddressField_ShowsItsError_ButNotForAnEmptyField()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                window.ConsoleByAddressCard.IsChecked = true;
                LeaveAddressField(window);
                Assert.Equal(string.Empty, window.AddressError.Text);

                window.AddressBox.Text = "a b";
                LeaveAddressField(window);
                Assert.Equal(Text("NEW_CAPTURE_ADDRESS_ERROR"), window.AddressError.Text);
                Assert.False(window.AddressPauseTimer.IsEnabled);
            }));
        }

        // Each device is reported with the type and name its capture tab shows, as the original look's dialog does.
        [Theory]
        [InlineData("ThisPc", false, DeviceType.LocalPC, "Local PC", CaptureType.WebProxy)]
        [InlineData("ThisPc", true, DeviceType.LocalPC, "Local PC", CaptureType.NetworkTrace)]
        [InlineData("DefaultConsole", true, DeviceType.XboxConsole, DefaultConsoleAddress, CaptureType.NetworkTrace)]
        [InlineData("ConsoleByAddress", false, DeviceType.XboxConsole, "devkit07", CaptureType.WebProxy)]
        [InlineData("GenericDevice", false, DeviceType.GenericProxyDevice, "Generic Device", CaptureType.WebProxy)]
        [InlineData("HandheldOrRemotePc", false, DeviceType.GenericProxyDevice, "Windows Handheld or Remote Windows Device", CaptureType.WebProxy)]
        public void AddCapture_ReportsTheDeviceAndCaptureType(
            string device, bool networkTrace, DeviceType type, string name, CaptureType capture)
        {
            _application.Run(_ =>
            {
                (bool? added, NewCaptureWindow window) = ShowDialog(window =>
                {
                    Card(window, Enum.Parse<NewCaptureWindow.Device>(device)).IsChecked = true;
                    window.AddressBox.Text = "devkit07";
                    window.NetworkTraceCard.IsChecked = networkTrace;
                    window.WebProxyCard.IsChecked = !networkTrace;
                    Click(window.AddButton);
                });

                Assert.True(added);
                Assert.Equal(AddDeviceWindow.AddDeviceResult.Success, window.Result);
                Assert.Equal(type, window.SelectedDeviceType);
                Assert.Equal(name, window.SelectedDeviceName);
                Assert.Equal(capture, window.SelectedCaptureType);
            });
        }

        [Fact]
        public void AddCapture_WithoutADefaultConsole_Fails()
        {
            _application.Run(_ =>
            {
                (bool? added, NewCaptureWindow window) = ShowDialog(window =>
                {
                    window.DefaultConsoleCard.IsChecked = true;
                    Click(window.AddButton);
                }, defaultConsoleAddress: () => string.Empty);

                Assert.False(added);
                Assert.Equal(AddDeviceWindow.AddDeviceResult.Failed_NoDefaultConsole, window.Result);
            });
        }

        [Fact]
        public void DoubleClickingACaptureType_AddsTheCapture()
        {
            _application.Run(_ =>
            {
                (bool? added, NewCaptureWindow window) = ShowDialog(window =>
                {
                    window.NetworkTraceCard.IsChecked = true;
                    window.NetworkTraceCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                    });
                });

                Assert.True(added);
                Assert.Equal(CaptureType.NetworkTrace, window.SelectedCaptureType);
            });
        }

        // Enter adds the capture, and Esc cancels, while focus is on a card: a card leaves Enter to the default button.
        [Fact]
        public void EnterAndEsc_ReachTheDialogsButtons_FromTheCards()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                Assert.False(KeyboardNavigation.GetAcceptsReturn(window.ThisPcCard));
                Assert.True(window.AddButton.IsDefault);
                Assert.True(window.CancelButton.IsCancel);
            }));
        }

        [Fact]
        public void Cards_AreRadioButtonsNamedByTheirTitles_WithTheirTextAsHelp_AndTheirPlaceInTheChoice()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                AutomationPeer address = UIElementAutomationPeer.CreatePeerForElement(window.ConsoleByAddressCard);
                Assert.Equal(AutomationControlType.RadioButton, address.GetAutomationControlType());
                Assert.Equal("Xbox console by address", address.GetName());
                Assert.Equal(Text("NEW_CAPTURE_CONSOLE_BY_ADDRESS_DESCRIPTION"), address.GetHelpText());
                Assert.Equal(3, address.GetPositionInSet());
                Assert.Equal(5, address.GetSizeOfSet());

                AutomationPeer trace = UIElementAutomationPeer.CreatePeerForElement(window.NetworkTraceCard);
                Assert.Equal("Network Trace", trace.GetName());
                Assert.Equal(
                    Text("NEW_CAPTURE_NETWORK_TRACE_DESCRIPTION") + " " + Text("NEW_CAPTURE_NOTE_PC_TRACE"),
                    trace.GetHelpText());
                Assert.Equal(2, trace.GetPositionInSet());
                Assert.Equal(2, trace.GetSizeOfSet());
            }));
        }

        // On a screen shorter than the dialog, such as a 1080p laptop at 150%, the cards scroll and Add capture and Cancel
        // stay in view.
        [Fact]
        public void OnAShortScreen_TheCardsScroll_AndTheButtonsStayInView()
        {
            _application.Run(_ => WithWindow(gdkInstalled: true, window =>
            {
                // Without the height of the screen the tests run on, the cards fit.
                window.MaxHeight = double.PositiveInfinity;
                window.UpdateLayout();
                WpfTestHost.Pump();
                Assert.Equal(0, window.Body.ScrollableHeight);
                window.MaxHeight = 420;
                window.UpdateLayout();
                WpfTestHost.Pump();

                Assert.True(window.Body.ScrollableHeight > 0);
                var content = (FrameworkElement)window.Content;
                foreach (Button button in new[] { window.AddButton, window.CancelButton })
                {
                    Point bottom = button.TranslatePoint(new Point(0, button.ActualHeight), content);
                    Assert.True(bottom.Y <= content.ActualHeight, $"{button.Content} ends at {bottom.Y}, below {content.ActualHeight}.");
                }
            }));
        }

        // A dialog that grows past the bottom of its screen, as when the address card is chosen, moves up; one that WPF
        // centers above the top of a short screen moves down, so its title bar shows; one taller than the screen keeps its
        // top on the screen; and one off the screen stays where it is.
        [Theory]
        [InlineData(100, 500, 100)]
        [InlineData(100, 640, 32)]
        [InlineData(-34, 569, 0)]
        [InlineData(100, 800, 0)]
        [InlineData(-12000, 700, -12000)]
        [InlineData(700, 300, 700)]
        public void TheDialog_StaysWithinItsScreen(double top, double height, double expected)
        {
            var workArea = new Rect(0, 0, 1280, 672);
            Assert.Equal(expected, NewCaptureWindow.TopWithin(workArea, top, height));
        }
        private static string Text(string key) => Localization.GetLocalizedString(key);

        private static NewCaptureWindow CreateWindow(bool gdkInstalled, Func<string> defaultConsoleAddress)
        {
            ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
            ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
            return new NewCaptureWindow(gdkInstalled, defaultConsoleAddress ?? (() => DefaultConsoleAddress))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000,
                Top = -12000,
                ShowActivated = false,
                Resources = look,
            };
        }

        private static void WithWindow(bool gdkInstalled, Action<NewCaptureWindow> test)
        {
            NewCaptureWindow window = CreateWindow(gdkInstalled, null);
            window.Show();
            try
            {
                WpfTestHost.Pump();
                test(window);
            }
            finally
            {
                window.Close();
            }
        }

        // Shows the dialog as New Capture does, runs CHOOSE in it, and returns what ShowDialog returned.
        private static (bool? Added, NewCaptureWindow Window) ShowDialog(
            Action<NewCaptureWindow> choose, Func<string> defaultConsoleAddress = null)
        {
            NewCaptureWindow window = CreateWindow(gdkInstalled: true, defaultConsoleAddress);
            Exception failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    choose(window);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                }
            }));
            bool? added = window.ShowDialog();
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return (added, window);
        }

        private static ChoiceCard Card(NewCaptureWindow window, NewCaptureWindow.Device device) => device switch
        {
            NewCaptureWindow.Device.DefaultConsole => window.DefaultConsoleCard,
            NewCaptureWindow.Device.ConsoleByAddress => window.ConsoleByAddressCard,
            NewCaptureWindow.Device.GenericDevice => window.GenericDeviceCard,
            NewCaptureWindow.Device.HandheldOrRemotePc => window.HandheldCard,
            _ => window.ThisPcCard,
        };

        private static void AssertTabStops(NewCaptureWindow window, ChoiceCard device, ChoiceCard captureType)
        {
            foreach (ChoiceCard card in window.ThisPcCard.Group().Concat(window.WebProxyCard.Group()))
            {
                Assert.True(card.IsTabStop == (card == device || card == captureType), $"{card.Title} is a Tab stop: {card.IsTabStop}");
            }
        }

        private static void Press(UIElement target, Key key)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
            WpfTestHost.Pump();
        }

        private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void LeaveAddressField(NewCaptureWindow window)
        {
            window.AddressBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, window.AddressBox, null)
            {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent,
            });
            WpfTestHost.Pump();
        }

        private static void WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                WpfTestHost.Pump();
                Thread.Sleep(5);
            }

            Assert.True(condition(), "The condition was not met within 5 seconds.");
        }
    }
}
