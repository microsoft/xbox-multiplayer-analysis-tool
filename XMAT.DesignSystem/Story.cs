// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace XMAT.DesignSystem
{
    // A page of the design system: a foundation of the new look, such as its colors, or one of its components, with the
    // files that define it and examples of it. Pages are XAML files in XMAT.DesignSystem/Stories, so their examples read
    // like the views that use them; hover, press and keyboard focus show live. StoryCatalog lists the pages. A page
    // follows one outline, leaving out the parts that don't apply: when to use it (StoryUsage), its variants and states
    // (StorySection), then its rules (StoryRules) with examples of following and breaking each.
    public class Story : UserControl
    {
        // The sections of the design system, in the order the window lists them. Every page is in one.
        internal static readonly string[] Sections = { "Foundations", "Components", "Patterns", "Layouts" };

        // The page's name: its title and its entry in the list of pages.
        public string Title { get; set; }

        // What the page shows, what it is for and where XMAT uses it.
        public string Summary { get; set; }

        // The section the page is listed under: one of Sections.
        public string Section { get; set; }

        // The files that define what the page shows, relative to the repository and separated by commas, such as
        // XMAT/Styles/Buttons.xaml.
        public string DefinedIn { get; set; }

        internal IReadOnlyList<string> DefinedInFiles =>
            (DefinedIn ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // A group of examples on a page: a variant of a component, or a part of a foundation, what it is for, and the style
    // or resources it uses.
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

        // The style's key, or for a control's default style its type, such as "Button (default style)"; on a foundation
        // page, the resource keys the section shows.
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

    // When to use a component, and when not, with what to use instead: the first block of a component's page. Each
    // entry is a sentence.
    public class StoryUsage : Control
    {
        public Collection<string> UseFor { get; } = new Collection<string>();

        public Collection<string> NotFor { get; } = new Collection<string>();
    }

    // A page's rules, each a StoryRule, under a heading.
    public class StoryRules : ItemsControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(StoryRules), new PropertyMetadata("Rules"));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(StoryRules));

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
    }

    // A rule from RuleCatalog: its ID, how firmly it holds, what it says and why, and how it's checked, with live
    // examples of following it and of breaking it as its items, each a RuleExample.
    public class StoryRule : ItemsControl
    {
        public static readonly DependencyProperty RuleIdProperty = DependencyProperty.Register(
            nameof(RuleId), typeof(string), typeof(StoryRule),
            new PropertyMetadata(null, (rule, e) => ((StoryRule)rule).Rule = RuleCatalog.Find((string)e.NewValue)));

        private static readonly DependencyPropertyKey RulePropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(Rule), typeof(DesignRule), typeof(StoryRule), new PropertyMetadata(null));

        public static readonly DependencyProperty RuleProperty = RulePropertyKey.DependencyProperty;

        public string RuleId
        {
            get => (string)GetValue(RuleIdProperty);
            set => SetValue(RuleIdProperty, value);
        }

        // The rule RuleId names, or null when the catalog has no such rule.
        public DesignRule Rule
        {
            get => (DesignRule)GetValue(RuleProperty);
            private set => SetValue(RulePropertyKey, value);
        }
    }

    // Whether an example follows its rule or breaks it.
    public enum RuleExampleKind
    {
        Do,
        Dont,
    }

    // A live example of following a rule (Do) or breaking it (Don't), with a caption that says what to notice.
    public class RuleExample : ContentControl
    {
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(RuleExampleKind), typeof(RuleExample));

        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RuleExample));

        public RuleExampleKind Kind
        {
            get => (RuleExampleKind)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public string Caption
        {
            get => (string)GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }
    }
}
