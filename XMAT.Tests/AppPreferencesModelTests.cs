// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Models;

namespace XMAT.Tests
{
    public class AppPreferencesModelTests
    {
        // Applying a theme XMAT doesn't offer would fail at startup, so saved settings only ever load Light or Dark.
        [Theory]
        [InlineData("Dark", "Dark")]
        [InlineData("Light", "Light")]
        [InlineData("Halo", "Light")]
        [InlineData(null, "Light")]
        public void SavedTheme_LoadsAsLightOrDark(string saved, string expected)
        {
            var preferences = new AppPreferencesModel();
            preferences.DeserializeFrom(new AppPreferencesModel { Theme = saved });
            Assert.Equal(expected, preferences.Theme);
        }

        // Comfort view is on unless it was turned off, including in settings saved before it existed.
        [Theory]
        [InlineData("{\"Theme\":\"Dark\",\"UseNewLook\":true}", true)]
        [InlineData("{\"ComfortView\":false}", false)]
        [InlineData("{\"ComfortView\":true}", true)]
        public void ComfortView_IsOnUnlessSavedOff(string saved, bool expected)
        {
            var preferences = new AppPreferencesModel();
            preferences.DeserializeFrom(JsonSerializer.Deserialize<AppPreferencesModel>(saved));
            Assert.Equal(expected, preferences.ComfortView);
        }

        [Fact]
        public void ComfortView_IsOnByDefaultAndSavedWithThePreferences()
        {
            Assert.True(new AppPreferencesModel().ComfortView);

            string saved = JsonSerializer.Serialize(new AppPreferencesModel { ComfortView = false });
            var preferences = new AppPreferencesModel();
            preferences.DeserializeFrom(JsonSerializer.Deserialize<AppPreferencesModel>(saved));
            Assert.False(preferences.ComfortView);
        }

        // The traffic overview is shown unless it was hidden, including in settings saved before it existed.
        [Theory]
        [InlineData("{\"Theme\":\"Dark\",\"ComfortView\":false}", true)]
        [InlineData("{\"ShowTrafficOverview\":false}", false)]
        [InlineData("{\"ShowTrafficOverview\":true}", true)]
        public void TrafficOverview_IsShownUnlessSavedHidden(string saved, bool expected)
        {
            Assert.True(new AppPreferencesModel().ShowTrafficOverview);
            var preferences = new AppPreferencesModel();
            preferences.DeserializeFrom(JsonSerializer.Deserialize<AppPreferencesModel>(saved));
            Assert.Equal(expected, preferences.ShowTrafficOverview);
        }
    }
}
