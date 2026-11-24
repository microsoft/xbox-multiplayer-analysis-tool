// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;

namespace XMAT.Tests.DesignSystem
{
    // XMAT/Themes/Palette.xaml is generated from XMAT/Themes/Palette.json. After changing Palette.json, regenerate it with
    //   $env:XMAT_PALETTE_WRITE = "1"; dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PaletteTests"
    // then clear XMAT_PALETTE_WRITE. The comparison is skipped while it is set.
    public class PaletteTests
    {
        internal const string WriteVariable = "XMAT_PALETTE_WRITE";

        [Fact]
        public void Palette_MatchesPaletteJson()
        {
            if (GenerationRequested())
            {
                Assert.False(IsContinuousIntegration(), $"{WriteVariable} must not be set in CI.");
                return;
            }

            string expected = PaletteGenerator.ToXaml(PaletteGenerator.Generate(PaletteGenerator.Load(PaletteGenerator.SettingsPath)));
            Assert.True(
                File.Exists(PaletteGenerator.PalettePath) && Normalize(File.ReadAllText(PaletteGenerator.PalettePath)) == Normalize(expected),
                $"COLOR-5: {PaletteGenerator.PalettePath} doesn't match Palette.json. Regenerate it as {nameof(PaletteTests)} describes.");
        }

        [Fact]
        public void WritesThePalette_WhenRequested()
        {
            if (!GenerationRequested())
            {
                return;
            }

            Assert.False(IsContinuousIntegration(), $"{WriteVariable} must not be set in CI.");
            File.WriteAllText(
                PaletteGenerator.PalettePath,
                PaletteGenerator.ToXaml(PaletteGenerator.Generate(PaletteGenerator.Load(PaletteGenerator.SettingsPath))));
        }

        // Themes pick steps by position, so a ramp must darken step by step, and each base color must be its own step.
        [Fact]
        public void EachRamp_DarkensStepByStep_AndHoldsItsBaseColor()
        {
            PaletteGenerator.Settings settings = PaletteGenerator.Load(PaletteGenerator.SettingsPath);
            IReadOnlyList<PaletteGenerator.Ramp> ramps = PaletteGenerator.Generate(settings);

            foreach (PaletteGenerator.Ramp ramp in ramps)
            {
                double[] lightness = ramp.Steps.Select(step => PaletteGenerator.OkLch(step).L).ToArray();
                for (int i = 1; i < lightness.Length; i++)
                {
                    Assert.True(
                        lightness[i] < lightness[i - 1],
                        $"{ramp.Name}{i + 1} ({ramp.Steps[i]}) isn't darker than {ramp.Name}{i} ({ramp.Steps[i - 1]}).");
                }
            }

            foreach (PaletteGenerator.ColorSettings color in settings.Colors)
            {
                PaletteGenerator.Ramp ramp = ramps.Single(candidate => candidate.Name == color.Name);
                Assert.Equal(color.Base.ToUpperInvariant(), ramp.Steps[color.BaseStep - 1]);
            }
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n");

        private static bool GenerationRequested() =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WriteVariable));

        private static bool IsContinuousIntegration() =>
            new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD" }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
    }
}
