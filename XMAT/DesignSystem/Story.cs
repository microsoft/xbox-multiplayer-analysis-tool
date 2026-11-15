// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;

namespace XMAT.DesignSystem
{
    // A page of the design system window: one component of the new look, the style file that defines it, and examples
    // of it in the states it can be set to. Pages are XAML files in DesignSystem/Stories, so their examples read like
    // the views that use the component; hover, press and keyboard focus show live. StoryCatalog lists the pages.
    public class Story : UserControl
    {
        // The component's name: the page's title and its entry in the list of components.
        public string Title { get; set; }

        // What the component is for and where XMAT shows it.
        public string Summary { get; set; }

        // The file that defines the component's styles, relative to XMAT, such as Styles/Buttons.xaml.
        public string StyleFile { get; set; }
    }

    // A group of examples on a page: a variant of the component, what it is for, and the style it uses.
    public class StorySection : ItemsControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(StorySection));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(StorySection));

        public static readonly DependencyProperty StyleNameProperty =
            DependencyProperty.Register(nameof(StyleName), typeof(string), typeof(StorySection));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        // The style's key, or for a control's default style its type, such as "Button (default style)".
        public string StyleName
        {
            get => (string)GetValue(StyleNameProperty);
            set => SetValue(StyleNameProperty, value);
        }
    }

    // One example in a section, captioned with the state it shows.
    public class StoryExample : ContentControl
    {
        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(StoryExample));

        public string Caption
        {
            get => (string)GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }
    }
}
