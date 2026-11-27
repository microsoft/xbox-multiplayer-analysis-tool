// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XMAT.Tests.DesignSystem
{
    // Generates XMAT's color palette, XMAT/Themes/Palette.xaml, from the base colors and settings in
    // XMAT/Themes/Palette.json. Each base color becomes a ramp of steps from light to dark in OKLCH, a color space whose
    // lightness and chroma follow what people see, and is itself one of the steps. Near the base color a step keeps the
    // base color's chroma; toward the ends it follows the shared chroma curve. "vivid" scales the chroma of the steps
    // lighter than the base color, fading in with distance from it, and each ramp's hue moves from hueDark at the dark
    // end to hueLight at the light end. The gray ramp has its own, finer steps at both ends, where the Light and Dark
    // themes' surfaces sit, and turns slightly cool toward its dark steps. Themes name roles with these steps.
    internal static class PaletteGenerator
    {
        private const double GamutTolerance = 1e-4;

        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        internal sealed class Settings
        {
            public string Description { get; set; }

            public ColorSettings[] Colors { get; set; }

            public GraySettings Gray { get; set; }

            public double[] ColorSteps { get; set; }

            public double[][] Chroma { get; set; }
        }

        internal sealed class ColorSettings
        {
            public string Name { get; set; }

            public string Base { get; set; }

            public int BaseStep { get; set; }

            public double HueLight { get; set; }

            public double HueDark { get; set; }

            public double Vivid { get; set; }
        }

        internal sealed class GraySettings
        {
            public double[] Steps { get; set; }

            public double Hue { get; set; }

            public double TintDark { get; set; }
        }

        // A ramp's steps as "#RRGGBB", lightest first. BaseStep is 1-based, or 0 for the gray, which has no base color.
        internal sealed record Ramp(string Name, int BaseStep, IReadOnlyList<string> Steps);

        internal static string SettingsPath => Path.Combine(HeadlessParity.RepositoryPaths.Root, "XMAT", "Themes", "Palette.json");

        internal static string PalettePath => Path.Combine(HeadlessParity.RepositoryPaths.Root, "XMAT", "Themes", "Palette.xaml");

        internal static Settings Load(string path) => JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options);

        internal static IReadOnlyList<Ramp> Generate(Settings settings)
        {
            var ramps = new List<Ramp> { new("Gray", 0, settings.Gray.Steps.Select(L => GrayStep(settings.Gray, L)).ToList()) };
            ramps.AddRange(settings.Colors.Select(color => new Ramp(color.Name, color.BaseStep, ColorRamp(settings, color))));
            return ramps;
        }

        // Palette.xaml: every step as a Color resource named for its ramp and step, such as Gray13 or Blue8.
        internal static string ToXaml(IReadOnlyList<Ramp> ramps)
        {
            var text = new StringBuilder();
            text.Append("<!-- Generated from Palette.json by XMAT.Tests/DesignSystem/PaletteGenerator.cs. Don't edit this file: change\r\n");
            text.Append("     the base colors in Palette.json and regenerate it (docs/design-system.md). Themes name roles with these\r\n");
            text.Append("     steps; views and styles use the roles, never the steps. -->\r\n");
            text.Append("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\r\n");
            text.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\r\n");
            foreach (Ramp ramp in ramps)
            {
                text.Append("\r\n");
                text.Append(ramp.BaseStep == 0
                    ? $"    <!-- {ramp.Name}: {ramp.Steps.Count} steps, lightest first -->\r\n"
                    : $"    <!-- {ramp.Name}: {ramp.Steps.Count} steps, lightest first; the base color {ramp.Steps[ramp.BaseStep - 1]} is step {ramp.BaseStep} -->\r\n");
                for (int i = 0; i < ramp.Steps.Count; i++)
                {
                    text.Append($"    <Color x:Key=\"{ramp.Name}{i + 1}\">#FF{ramp.Steps[i][1..]}</Color>\r\n");
                }
            }

            text.Append("\r\n</ResourceDictionary>\r\n");
            return text.ToString();
        }

        private static List<string> ColorRamp(Settings settings, ColorSettings color)
        {
            double[] lightness = settings.ColorSteps;
            (double L0, double C0, double H0) = OkLch(color.Base);
            int anchor = color.BaseStep - 1;
            double shift = L0 - lightness[anchor];
            double target0 = Interpolate(settings.Chroma, L0);
            int reach = Math.Max(anchor, lightness.Length - 1 - anchor);
            double[][] hues = { new[] { lightness[^1], color.HueDark }, new[] { L0, H0 }, new[] { lightness[0], color.HueLight } };
            var steps = new List<string>();
            for (int i = 0; i < lightness.Length; i++)
            {
                if (i == anchor)
                {
                    steps.Add(color.Base.ToUpperInvariant());
                    continue;
                }

                // The base color moves its neighbors with it; the move fades out toward both ends.
                double nearness = 1 - Math.Abs(i - anchor) / (double)reach;
                double L = lightness[i] + shift * nearness;
                double hue = Interpolate(hues, L);
                double follow = 1 + (C0 / target0 - 1) * nearness;
                double vivid = L > L0 ? 1 + (color.Vivid - 1) * (1 - nearness) : 1.0;
                steps.Add(ToHex(L, Interpolate(settings.Chroma, L) * follow * vivid, hue));
            }

            return steps;
        }

        // The gray's tint is TintDark at its darkest step, 60% of that at lightness 0.45, and none from 0.80 up.
        private static string GrayStep(GraySettings gray, double L)
        {
            double[][] tint = { new[] { 0.19, gray.TintDark }, new[] { 0.45, gray.TintDark * 0.6 }, new[] { 0.80, 0.0 } };
            return ToHex(L, Interpolate(tint, L), gray.Hue);
        }

        // Piecewise-linear interpolation over (x, y) points, held at the ends.
        private static double Interpolate(double[][] points, double x)
        {
            double[][] sorted = points.OrderBy(point => point[0]).ToArray();
            if (x <= sorted[0][0])
            {
                return sorted[0][1];
            }

            if (x >= sorted[^1][0])
            {
                return sorted[^1][1];
            }

            for (int i = 0; i < sorted.Length - 1; i++)
            {
                double x0 = sorted[i][0], y0 = sorted[i][1], x1 = sorted[i + 1][0], y1 = sorted[i + 1][1];
                if (x0 <= x && x <= x1)
                {
                    return y0 + (x - x0) / (x1 - x0) * (y1 - y0);
                }
            }

            return sorted[^1][1];
        }

        internal static (double L, double C, double H) OkLch(string hex)
        {
            (double L, double a, double b) = OkLab(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16),
                Convert.ToInt32(hex.Substring(5, 2), 16));
            double hue = (180.0 / Math.PI) * Math.Atan2(b, a) % 360;
            return (L, double.Hypot(a, b), hue < 0 ? hue + 360 : hue);
        }

        private static (double L, double a, double b) OkLab(int red, int green, int blue)
        {
            double r = ToLinear(red), g = ToLinear(green), b = ToLinear(blue);
            double l = CubeRoot(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = CubeRoot(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = CubeRoot(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        private static (double R, double G, double B) ToLinearRgb(double L, double C, double H)
        {
            double a = C * Math.Cos(Math.PI / 180.0 * H);
            double b = C * Math.Sin(Math.PI / 180.0 * H);
            double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
            double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
            double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
            return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        }

        private static bool InGamut(double L, double C, double H)
        {
            (double r, double g, double b) = ToLinearRgb(L, C, H);
            return new[] { r, g, b }.All(value => value >= -GamutTolerance && value <= 1 + GamutTolerance);
        }

        // The most chroma that sRGB can show at this lightness and hue.
        private static double MaxChroma(double L, double H)
        {
            double low = 0, high = 0.4;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2;
                if (InGamut(L, middle, H))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static string ToHex(double L, double C, double H)
        {
            (double r, double g, double b) = ToLinearRgb(L, Math.Min(C, MaxChroma(L, H)), H);
            return string.Create(CultureInfo.InvariantCulture, $"#{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}");
        }

        private static int ToByte(double linear) => (int)Math.Round(255 * ToSrgb(linear), MidpointRounding.ToEven);

        private static double ToLinear(int channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static double ToSrgb(double linear)
        {
            double c = Math.Clamp(linear, 0.0, 1.0);
            return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        }

        private static double CubeRoot(double value) => Math.CopySign(Math.Pow(Math.Abs(value), 1.0 / 3), value);
    }
}
