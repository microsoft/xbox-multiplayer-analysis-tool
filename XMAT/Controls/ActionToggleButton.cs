// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace XMAT
{
    // A toggle whose label names the action it performs next, such as Start Capture and Stop Capture. Screen readers
    // announce it as a button with that label: an on or off state as well would contradict the label.
    public class ActionToggleButton : ToggleButton
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ActionToggleButtonAutomationPeer(this);

        private void InvokeFromAutomation() => OnClick();

        private sealed class ActionToggleButtonAutomationPeer : ButtonBaseAutomationPeer, IInvokeProvider
        {
            internal ActionToggleButtonAutomationPeer(ActionToggleButton owner) : base(owner)
            {
            }

            protected override string GetClassNameCore() => nameof(ActionToggleButton);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

            void IInvokeProvider.Invoke()
            {
                if (!IsEnabled())
                {
                    throw new ElementNotEnabledException();
                }

                // Like a button's own peer, click asynchronously, so a dialog that the click opens doesn't block the
                // screen reader's call.
                Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new DispatcherOperationCallback(_ =>
                    {
                        ((ActionToggleButton)Owner).InvokeFromAutomation();
                        return null;
                    }), null);
            }
        }
    }
}
