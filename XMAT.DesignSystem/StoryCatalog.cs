// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.DesignSystem
{
    // The design system's pages, in the order the window lists them. A component added to the new look gets a page
    // here (docs/design-system.md).
    internal static class StoryCatalog
    {
        internal static IReadOnlyList<Func<Story>> Pages { get; } = new Func<Story>[]
        {
            () => new ButtonsStory(),
            () => new ToolbarStory(),
            () => new TextInputsStory(),
            () => new ChoicesStory(),
            () => new ChoiceCardsStory(),
            () => new TabsStory(),
            () => new MenusStory(),
            () => new ScrollBarsStory(),
        };
    }
}
