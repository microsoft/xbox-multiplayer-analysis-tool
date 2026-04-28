// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class UriToMethodConverterTests
    {
        private UriToMethodConverter LoadConverterFromCsv(string csvContent)
        {
            var converter = new UriToMethodConverter();
            using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvContent)));
            converter.LoadMap(reader);
            return converter;
        }

        [Fact]
        public void GetService_ReturnsNull_ForUnknownService()
        {
            var converter = LoadConverterFromCsv(
                "\"presence.xboxlive.com\",\"CppMethod\",\"WinRTMethod\",\"CMethod\"\n"
            );

            Assert.Null(converter.GetService("unknown.service.com"));
        }

        [Fact]
        public void GetService_ReturnsMethods_ForKnownService()
        {
            var converter = LoadConverterFromCsv(
                "\"presence.xboxlive.com\",\"PresenceCpp\",\"PresenceWinRT\",\"PresenceC\"\n"
            );

            var result = converter.GetService("presence.xboxlive.com");
            Assert.NotNull(result);
            Assert.Equal("PresenceCpp", result.Item1);
            Assert.Equal("PresenceWinRT", result.Item2);
            Assert.Equal("PresenceC", result.Item3);
        }

        [Fact]
        public void GetMethod_ReturnsNull_ForUnknownUri()
        {
            var converter = LoadConverterFromCsv(
                "\"GET /users/{xuid}/profile\",\"GetProfileCpp\",\"GetProfileWinRT\",\"GetProfileC\"\n"
            );

            Assert.Null(converter.GetMethod("/unknown/path", true));
        }

        [Fact]
        public void GetMethod_MatchesGetEndpoint()
        {
            var converter = LoadConverterFromCsv(
                "\"GET /users/{xuid}/profile\",\"GetProfileCpp\",\"GetProfileWinRT\",\"GetProfileC\"\n"
            );

            var result = converter.GetMethod("/users/12345/profile", true);
            Assert.NotNull(result);
            Assert.Equal("GetProfileCpp", result.Item1);
        }

        [Fact]
        public void GetMethod_MatchesNonGetEndpoint()
        {
            var converter = LoadConverterFromCsv(
                "\"POST /users/{xuid}/profile\",\"SetProfileCpp\",\"SetProfileWinRT\",\"SetProfileC\"\n"
            );

            var result = converter.GetMethod("/users/12345/profile", false);
            Assert.NotNull(result);
            Assert.Equal("SetProfileCpp", result.Item1);
        }

        [Fact]
        public void GetMethod_DoesNotMatchGet_ForNonGetRequest()
        {
            var converter = LoadConverterFromCsv(
                "\"GET /users/{xuid}/profile\",\"GetProfileCpp\",\"GetProfileWinRT\",\"GetProfileC\"\n"
            );

            Assert.Null(converter.GetMethod("/users/12345/profile", false));
        }

        [Fact]
        public void GetServices_ReturnsAllServices()
        {
            var converter = LoadConverterFromCsv(
                "\"service1.com\",\"Cpp1\",\"WinRT1\",\"C1\"\n" +
                "\"service2.com\",\"Cpp2\",\"WinRT2\",\"C2\"\n"
            );

            var services = converter.GetServices();
            Assert.Equal(2, services.Count);
            Assert.True(services.ContainsKey("service1.com"));
            Assert.True(services.ContainsKey("service2.com"));
        }

        [Fact]
        public void LoadMap_SkipsRowsWithWrongColumnCount()
        {
            var converter = LoadConverterFromCsv(
                "\"Column1\",\"Column2\"\n" +  // Only 2 columns - should be skipped
                "\"service.com\",\"Cpp\",\"WinRT\",\"C\"\n"
            );

            var services = converter.GetServices();
            Assert.Single(services);
        }
    }
}
