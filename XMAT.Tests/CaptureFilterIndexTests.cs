// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class CaptureFilterIndexTests
    {
        [Fact]
        public void ExistingOptions_KeepTheirIdentityAndLiveCheckedState()
        {
            var option = new CheckedListItem("example.test", false);
            var source = new ObservableCollection<CheckedListItem> { option };
            var index = new CaptureFilterIndex();
            index.SetSource(source);
            index.EnsureValue(option.Text);

            Assert.Same(option, Assert.Single(source));
            Assert.False(index.IsChecked(option.Text));
            option.IsChecked = true;
            Assert.True(index.IsChecked(option.Text));
        }

        [Fact]
        public void EnsureValue_AddsMissingValuesOnceAndPreservesOrdinalMatching()
        {
            var source = new ObservableCollection<CheckedListItem> { new("Host", false) };
            var index = new CaptureFilterIndex();
            index.SetSource(source);
            index.EnsureValue("host");
            index.EnsureValue("host");
            index.EnsureValue(null);
            index.EnsureValue(string.Empty);

            Assert.Equal(2, source.Count);
            Assert.False(index.IsChecked("Host"));
            Assert.True(index.IsChecked("host"));
        }

        [Fact]
        public void CollectionChanges_PreserveTheFirstMatchingOptionsCheckedState()
        {
            var first = new CheckedListItem("same", false);
            var second = new CheckedListItem("same", true);
            var source = new ObservableCollection<CheckedListItem> { first };
            var index = new CaptureFilterIndex();
            index.SetSource(source);
            source.Add(second);
            Assert.False(index.IsChecked("same"));
            source.Move(1, 0);
            Assert.True(index.IsChecked("same"));
            source.Remove(second);
            Assert.False(index.IsChecked("same"));
            source[0] = new CheckedListItem("replacement");
            Assert.False(index.IsChecked("same"));
            Assert.True(index.IsChecked("replacement"));
            source.Clear();
            index.EnsureValue("same");
            Assert.True(index.IsChecked("same"));
        }

        [Fact]
        public void RenamingOptions_UpdatesMembershipWithoutLosingCheckedState()
        {
            var option = new CheckedListItem("old", false);
            var source = new ObservableCollection<CheckedListItem> { option };
            var index = new CaptureFilterIndex();
            index.SetSource(source);
            option.Text = "new";
            index.EnsureValue("new");
            index.EnsureValue("old");

            Assert.Equal(2, source.Count);
            Assert.Same(option, source[0]);
            Assert.False(index.IsChecked("new"));
            Assert.True(index.IsChecked("old"));
        }

        [Fact]
        public void ReplacingOrDetachingSource_DoesNotObserveOldOptions()
        {
            var original = new ObservableCollection<CheckedListItem> { new("old") };
            var replacement = new ObservableCollection<CheckedListItem> { new("new", false) };
            var index = new CaptureFilterIndex();
            index.SetSource(original);
            index.SetSource(replacement);
            original.Add(new CheckedListItem("ignored"));
            original[0].Text = "renamed";

            Assert.False(index.IsChecked("old"));
            Assert.False(index.IsChecked("ignored"));
            Assert.False(index.IsChecked("renamed"));
            Assert.False(index.IsChecked("new"));
            replacement[0].IsChecked = true;
            Assert.True(index.IsChecked("new"));
            index.SetSource(null);
            replacement.Add(new CheckedListItem("detached"));
            Assert.False(index.IsChecked("new"));
            Assert.False(index.IsChecked("detached"));
        }

        [Fact]
        public void NullAndEmptyExistingValues_RemainValidFilterChoices()
        {
            var source = new ObservableCollection<CheckedListItem> { new(null), new(string.Empty) };
            var index = new CaptureFilterIndex();
            index.SetSource(source);

            Assert.True(index.IsChecked(null));
            Assert.True(index.IsChecked(string.Empty));
            source[0].IsChecked = false;
            Assert.False(index.IsChecked(null));
        }

        [Fact]
        public void KnownValueLookups_DoNotAllocatePerLookupOrEnumerateOptionLists()
        {
            var source = new ObservableCollection<CheckedListItem>(
                Enumerable.Range(0, 10000).Select(number => new CheckedListItem($"host-{number}")));
            var index = new CaptureFilterIndex();
            index.SetSource(source);
            index.EnsureValue(source[0].Text);
            Assert.True(index.IsChecked(source[0].Text));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var selected = 0;
            for (var lookup = 0; lookup < 100000; lookup++)
            {
                var text = source[lookup % source.Count].Text;
                index.EnsureValue(text);
                if (index.IsChecked(text))
                {
                    selected++;
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(100000, selected);
            Assert.Equal(10000, source.Count);
            Assert.InRange(allocated, 0, 16384);
            index.SetSource(null);
        }
    }
}
