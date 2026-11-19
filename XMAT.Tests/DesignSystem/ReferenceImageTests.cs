// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit.Abstractions;
using XMAT.DesignSystem;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    // Pictures of every design system page, kept in XMAT.Tests/DesignSystem/Reference, so a change to a component shows
    // in review as a changed picture. Each page is rendered on its own in a window off screen, so controls that start
    // once they load, such as the capture timeline, draw their data: 880px wide at 96 DPI and without animations, in
    // Light with Comfort view, in Dark with Comfort view off, and in the Night sky contrast theme. The Menus page's
    // context menu is pictured open. After an intended change, update the pictures with
    //   $env:XMAT_REFERENCE_WRITE = "1"; dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReferenceImageTests"
    // then clear XMAT_REFERENCE_WRITE and include the changed pictures with the change. A page that renders differently
    // fails, writing what it rendered and a picture of the differences to XMAT.Tests/TestResults/DesignSystem. Where
    // pictures render differently for reasons outside XMAT, such as another version of Windows' fonts, setting
    // XMAT_REFERENCE_IMAGES=report reports the differences without failing.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ReferenceImageTests
    {
        internal const string WriteVariable = "XMAT_REFERENCE_WRITE";
        internal const string ModeVariable = "XMAT_REFERENCE_IMAGES";

        private const double PageWidth = 880;

        // A pixel differs when one of its color channels moves by more than this.
        private const int Tolerance = 24;

        private static readonly string ReferenceFolder =
            Path.Combine(RepositoryPaths.Root, "XMAT.Tests", "DesignSystem", "Reference");

        private static readonly string ResultsFolder =
            Path.Combine(RepositoryPaths.Root, "XMAT.Tests", "TestResults", "DesignSystem");

        // Each picture's theme, contrast theme and Comfort view.
        private static readonly Dictionary<string, (string Theme, string Contrast, bool ComfortView)> Variants = new()
        {
            ["light"] = ("Light", null, true),
            ["dark-dense"] = ("Dark", null, false),
            ["night-sky"] = ("Light", "Night sky", true),
        };

        private readonly CaptureUiApplicationFixture _application;
        private readonly ITestOutputHelper _output;

        public ReferenceImageTests(CaptureUiApplicationFixture application, ITestOutputHelper output)
        {
            _application = application;
            _output = output;
        }

        public static TheoryData<int, string> Pages()
        {
            var pages = new TheoryData<int, string>();
            for (int page = 0; page < StoryCatalog.Pages.Count; page++)
            {
                foreach (string variant in Variants.Keys)
                {
                    pages.Add(page, variant);
                }
            }

            return pages;
        }

        public static TheoryData<string> AllVariants() => new(Variants.Keys);

        [Theory]
        [MemberData(nameof(Pages))]
        public void Page_MatchesItsReferenceImage(int page, string variant)
        {
            _application.Run(window => WithoutAnimations(() =>
            {
                Story story = StoryCatalog.Pages[page]();
                ResourceDictionary look = Look(variant);
                var host = new Border
                {
                    Width = PageWidth,
                    Padding = new Thickness(16),
                    UseLayoutRounding = true,
                    Resources = look,
                    Child = story,
                };
                host.SetResourceReference(Border.BackgroundProperty, "SurfaceCanvasBrush");
                host.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                host.SetResourceReference(TextElement.FontSizeProperty, "BaseFontSize");
                // The window never activates, so nothing on the page takes the keyboard focus; the canvas gives the page
                // its own height.
                window.ShowActivated = false;
                window.Content = new Canvas { Children = { host } };
                window.Show();
                WpfTestHost.Pump();
                Assert.True(VisualTreeHelper.GetDpi(host).PixelsPerInchX == 96,
                    "The test host isn't at 96 DPI. XMAT.Tests.runsettings runs it without DPI awareness.");
                Check(Capture(host, host.Background), $"{FileName(story)}-{variant}", story.Title);
            }));
        }

        [Theory]
        [MemberData(nameof(AllVariants))]
        public void OpenContextMenu_MatchesItsReferenceImage(string variant)
        {
            _application.Run(_ => WithoutAnimations(() =>
            {
                var story = new MenusStory();
                ContextMenu menu = story.SessionMenu;
                ResourceDictionary look = Look(variant);
                menu.Resources = look;
                menu.UseLayoutRounding = true;
                Check(Capture(menu, (Brush)look["SurfaceCanvasBrush"]), $"menus-context-menu-{variant}", story.Title);
            }));
        }

        private static ResourceDictionary Look(string variant)
        {
            (string theme, string contrast, bool comfortView) = Variants[variant];
            ResourceDictionary look = StoryPreview.CreateLook();
            StoryPreview.Apply(
                look,
                theme,
                ContrastTheme.All.SingleOrDefault(candidate => candidate.Name == contrast),
                comfortView,
                windowsHighContrast: false,
                textScale: 1.0);
            return look;
        }

        // ELEMENT at 96 DPI, on BACKGROUND: a page in its window, or an element laid out on its own, such as an open
        // context menu. The test host runs without DPI awareness (XMAT.Tests.runsettings), so Windows gives it a system
        // DPI of 96 whatever scale it was signed in with; text measured at another scale would make pages a few pixels
        // taller or shorter. An element laid out on its own also takes the bitmap's DPI, so the pictures are the same on
        // any machine.
        private static BitmapSource Capture(FrameworkElement element, Brush background)
        {
            if (VisualTreeHelper.GetParent(element) == null)
            {
                VisualTreeHelper.SetRootDpi(element, new DpiScale(1, 1));
                double available = element.Width > 0 ? element.Width : double.PositiveInfinity;
                element.Measure(new Size(available, double.PositiveInfinity));
                element.Arrange(new Rect(element.DesiredSize));
            }

            element.UpdateLayout();
            WpfTestHost.Pump();
            element.UpdateLayout();

            int width = (int)Math.Ceiling(element.DesiredSize.Width);
            int height = (int)Math.Ceiling(element.DesiredSize.Height);
            var backdrop = new DrawingVisual();
            using (DrawingContext context = backdrop.RenderOpen())
            {
                context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(backdrop);
            bitmap.Render(element);
            bitmap.Freeze();
            return bitmap;
        }

        private void Check(BitmapSource actual, string name, string page)
        {
            string reference = Path.Combine(ReferenceFolder, name + ".png");
            if (WriteRequested())
            {
                Assert.False(IsContinuousIntegration(), $"{WriteVariable} must not be set in CI.");
                Directory.CreateDirectory(ReferenceFolder);
                Save(actual, reference);
                return;
            }

            Assert.True(File.Exists(reference),
                $"{name}.png is missing from XMAT.Tests/DesignSystem/Reference. Create it as " +
                $"{nameof(ReferenceImageTests)} describes.");
            string difference = Compare(Pixels(Load(reference)), Pixels(actual), out BitmapSource differences);
            if (difference == null)
            {
                return;
            }

            Directory.CreateDirectory(ResultsFolder);
            Save(actual, Path.Combine(ResultsFolder, name + ".png"));
            if (differences != null)
            {
                Save(differences, Path.Combine(ResultsFolder, name + "-differences.png"));
            }

            string message = $"The {page} page's picture {name}.png {difference}. " +
                (differences == null
                    ? "What it rendered is in XMAT.Tests/TestResults/DesignSystem. "
                    : "What it rendered, and its differences in magenta, are in " +
                      "XMAT.Tests/TestResults/DesignSystem. ") +
                $"If the change is intended, update the pictures as {nameof(ReferenceImageTests)} describes.";
            if (ReportOnly())
            {
                _output.WriteLine(message);
                return;
            }

            Assert.Fail(message);
        }

        // Null when the pictures match; otherwise how they differ, with DIFFERENCES showing where.
        private static string Compare(
            (int Width, int Height, byte[] Bytes) expected,
            (int Width, int Height, byte[] Bytes) actual,
            out BitmapSource differences)
        {
            differences = null;
            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                return $"is {actual.Width}x{actual.Height} pixels rather than {expected.Width}x{expected.Height}";
            }

            byte[] marked = new byte[expected.Bytes.Length];
            int differing = 0;
            for (int index = 0; index < expected.Bytes.Length; index += 4)
            {
                bool differs = false;
                for (int channel = 0; channel < 4; channel++)
                {
                    differs |= Math.Abs(expected.Bytes[index + channel] - actual.Bytes[index + channel]) > Tolerance;
                }

                // The expected picture faded, with differing pixels in magenta.
                int gray = (expected.Bytes[index] + expected.Bytes[index + 1] + expected.Bytes[index + 2]) / 3;
                byte faded = (byte)(192 + gray / 4);
                marked[index] = differs ? (byte)255 : faded;
                marked[index + 1] = differs ? (byte)0 : faded;
                marked[index + 2] = differs ? (byte)255 : faded;
                marked[index + 3] = 255;
                differing += differs ? 1 : 0;
            }

            if (differing == 0)
            {
                return null;
            }

            differences = BitmapSource.Create(
                expected.Width, expected.Height, 96, 96, PixelFormats.Bgra32, null, marked, expected.Width * 4);
            return $"differs in {differing} pixels";
        }

        private static (int Width, int Height, byte[] Bytes) Pixels(BitmapSource image)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
            return (converted.PixelWidth, converted.PixelHeight, bytes);
        }

        private static BitmapSource Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        private static void Save(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        // A page's file name: its class without "Story", in lowercase words joined by hyphens, such as text-inputs.
        private static string FileName(Story story) =>
            Regex.Replace(story.GetType().Name[..^"Story".Length], "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();

        private static void WithoutAnimations(Action render)
        {
            bool? motion = MotionSettings.Current.Override;
            MotionSettings.Current.Override = false;
            try
            {
                render();
            }
            finally
            {
                MotionSettings.Current.Override = motion;
            }
        }

        private static bool WriteRequested() =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WriteVariable));

        private static bool ReportOnly() =>
            string.Equals(Environment.GetEnvironmentVariable(ModeVariable), "report", StringComparison.OrdinalIgnoreCase);

        private static bool IsContinuousIntegration() =>
            new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD" }
                .Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
    }
}
