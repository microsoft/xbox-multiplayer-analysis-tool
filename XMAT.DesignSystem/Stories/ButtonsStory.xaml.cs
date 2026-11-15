// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.DesignSystem
{
    public partial class ButtonsStory : Story
    {
        public ButtonsStory()
        {
            InitializeComponent();
            NoFilterExample.Content = FiltersButton(activeFilters: 0);
            ActiveFilterExample.Content = FiltersButton(activeFilters: 1);
        }

        // The Filters button as FilterOverflowHost makes it, marked with how many filters are on.
        private static FilterOverflowButton FiltersButton(int activeFilters)
        {
            var button = new FilterOverflowButton();
            button.SetResourceReference(StyleProperty, "FilterOverflowButtonStyle");
            button.ShowActiveFilters(activeFilters);
            return button;
        }
    }
}
