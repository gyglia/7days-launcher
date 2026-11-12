using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

partial class Installer
{
    sealed partial class LauncherWindow
    {
        const int SceneWidth = 920, SceneHeight = 614;
        readonly Canvas scene;
        readonly Grid atmosphere;
        readonly Canvas blood;
        readonly Rectangle warmLight, redLight, redSpill, coolShade;
        readonly Image redScene;
        readonly Button play, update, gear, browse, retry, remove;
        readonly TextBlock status, folder, version;
        readonly ProgressBar progress;
        readonly Border settingsPanel;
        readonly StackPanel settingsContent;
        readonly TextBox detailsText;
        readonly TextBlock panelTitle;
        readonly BitmapSource artwork;
        readonly List<DispatcherTimer> timers = new List<DispatcherTimer>();
        readonly Style buttonStyle;
        readonly Random random = new Random(37);
        bool effects = true;
        Image bloodProgress, bloodProgressTrack; RectangleGeometry bloodProgressClip;
        TextBlock progressNumber;

        public LauncherWindow(bool preview)
        {
            previewMode = preview;
            bundled = ParseManifest(ResourceText("manifest.json"));
            Title = preview ? "7 Days to Die — предпросмотр" : "7 Days to Die";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = SceneWidth; Height = SceneHeight;
            double fit = Math.Min(1, Math.Min((SystemParameters.WorkArea.Width - 24) / Width, (SystemParameters.WorkArea.Height - 24) / Height));
            Width *= fit; Height *= fit;
            using (Stream stream = OpenResource("MoonOutpost.png")) {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); artwork = image;
            }
            using (Stream stream = OpenResource("JAGO.ico")) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            buttonStyle = (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                <Setter Property='FontFamily' Value='Segoe UI'/><Setter Property='FontSize' Value='14'/><Setter Property='FontWeight' Value='SemiBold'/>
                <Setter Property='Foreground' Value='#F1EAE1'/><Setter Property='Background' Value='#E519191B'/>
                <Setter Property='BorderBrush' Value='#506B635E'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Cursor' Value='Hand'/>
                <Setter Property='Padding' Value='14,8'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
                <Border x:Name='shell' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='9'>
                <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/></Border>
                <ControlTemplate.Triggers>
                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='shell' Property='BorderBrush' Value='#CFCC9D86'/></Trigger>
                <Trigger Property='IsPressed' Value='True'><Setter TargetName='shell' Property='Opacity' Value='0.7'/></Trigger>
                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='shell' Property='BorderBrush' Value='#FFF0D7C1'/></Trigger>
                <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
                </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
            scene = new Canvas { Width = SceneWidth, Height = SceneHeight };
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = scene };
            var mask = new ImageBrush(artwork) { Stretch = Stretch.Fill }; mask.Freeze();
            // One fixed alpha mask for every state: lighting can never alter the silhouette.
            atmosphere = new Grid { Width = SceneWidth, Height = SceneHeight, OpacityMask = mask };
            scene.Children.Add(atmosphere);
            using (Stream stream = OpenResource("MoonOutpost-White.png")) {
                var whiteArt = new BitmapImage(); whiteArt.BeginInit(); whiteArt.CacheOption = BitmapCacheOption.OnLoad;
                whiteArt.StreamSource = stream; whiteArt.EndInit(); whiteArt.Freeze();
                atmosphere.Children.Add(new Image { Source = OpaqueColor(whiteArt), Stretch = Stretch.Fill });
            }
            redScene = new Image { Source = OpaqueColor(artwork), Stretch = Stretch.Fill, Opacity = 0, IsHitTestVisible = false };
            atmosphere.Children.Add(redScene);
            using (var stream = OpenResource("PorchLamp-Off.png")) {
                var off = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // Only the porch and its nearby reflection change; the rest of the art is untouched.
                var crop = new CroppedBitmap(off, new Int32Rect(805, 620, 270, 340));
                var fadeEdges = new LinearGradientBrush();
                fadeEdges.StartPoint = new Point(0, 0); fadeEdges.EndPoint = new Point(1, 0);
                fadeEdges.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                fadeEdges.GradientStops.Add(new GradientStop(Colors.Black, .13));
                fadeEdges.GradientStops.Add(new GradientStop(Colors.Black, .83));
                fadeEdges.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                lampShade = new Image { Source = crop, Width = 270.0 * SceneWidth / 1536, Height = 340.0 * SceneHeight / 1024,
                    Stretch = Stretch.Fill, Opacity = 1, OpacityMask = fadeEdges, IsHitTestVisible = false };
                var patch = new Canvas { IsHitTestVisible = false };
                var verticalMask = new LinearGradientBrush();
                verticalMask.StartPoint = new Point(0, 0); verticalMask.EndPoint = new Point(0, 1);
                verticalMask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                verticalMask.GradientStops.Add(new GradientStop(Colors.Black, .08));
                verticalMask.GradientStops.Add(new GradientStop(Colors.Black, .85));
                verticalMask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                patch.Width = lampShade.Width; patch.Height = lampShade.Height; patch.OpacityMask = verticalMask;
                patch.Children.Add(lampShade);
                patch.HorizontalAlignment = HorizontalAlignment.Left; patch.VerticalAlignment = VerticalAlignment.Top;
                patch.Margin = new Thickness(805.0 * SceneWidth / 1536, 620.0 * SceneHeight / 1024, 0, 0);
                atmosphere.Children.Add(patch);
            }
            AddZombie();
            coolShade = new Rectangle { Fill = Brush("#182934"), Opacity = .08, IsHitTestVisible = false };
            atmosphere.Children.Add(coolShade);
            warmLight = Glow("#FF8A34", new Point(.70, .65), .43);
            redLight = Glow("#F32916", new Point(.16, .22), .51);
            redSpill = Glow("#E93220", new Point(.42, .43), 1.15);
            atmosphere.Children.Add(warmLight); atmosphere.Children.Add(redLight); atmosphere.Children.Add(redSpill);
            AddEmbers();
            atmosphere.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) {} }
            };

            play = ButtonAt("Играть", 310, 510, 156, 48, scene);
            play.Background = Brush("#F075231F"); play.BorderBrush = Brush("#A9C16048"); play.FontSize = 16;
            play.Cursor = Cursors.Hand;
            update = ButtonAt("Обновить", 424, 518, 72, 48, scene);
            update.Visibility = Visibility.Collapsed;
            update.ToolTip = "Обновить";

            MakePlayMark(play);

            gear = ButtonAt("\uE713", 34, 523, 42, 42, scene);
            gear.FontFamily = new FontFamily("Segoe MDL2 Assets"); gear.FontSize = 19; gear.ToolTip = "Настройки";
            AutomationProperties.SetName(gear, "Настройки");
            MakeRustyGear(gear);
            status = TextAt("", 216, 479, 576, 25, 12, scene); status.TextAlignment = TextAlignment.Center;
            status.Foreground = Brush("#E2D5C8"); status.TextTrimming = TextTrimming.CharacterEllipsis;
            progress = new ProgressBar { Width = 310, Height = 2, Minimum = 0, Maximum = 100, Foreground = Brush("#CD6743"),
                Background = Brush("#50231F1D"), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
            Place(progress, 322, 572, scene);
            progress.Opacity = 0;
            using (var stream = OpenResource("BloodProgress.png")) {
                var art = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var crop = new CroppedBitmap(art, new Int32Rect(0, (int)(art.PixelHeight * .44), art.PixelWidth, (int)(art.PixelHeight * .20)));
                bloodProgressClip = new RectangleGeometry(new Rect(0, 0, 0, 18));
                bloodProgress = new Image { Source = crop, Width = 520, Height = 18, Stretch = Stretch.Fill, Clip = bloodProgressClip, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                bloodProgressTrack = new Image { Source = crop, Width = 520, Height = 18, Stretch = Stretch.Fill, Opacity = .28, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                Place(bloodProgressTrack, 200, 562, scene);
                Place(bloodProgress, 200, 562, scene);
            }
            progress.ValueChanged += delegate { DrawBloodProgress(); };
            progress.IsVisibleChanged += delegate { DrawBloodProgress(); };
            progressNumber = TextAt("", 410, 543, 100, 18, 14, scene);
            progressNumber.FontFamily = new FontFamily("Impact, Arial Black");
            progressNumber.FontWeight = FontWeights.Normal;

            progressNumber.Foreground = Brush("#4D1716");
            progressNumber.TextAlignment = TextAlignment.Center;
            progressNumber.IsHitTestVisible = false;
            progressNumber.Visibility = Visibility.Collapsed;
            blood = new Canvas { Width = SceneWidth, Height = SceneHeight, OpacityMask = mask, IsHitTestVisible = false };
            blood.Clip = new RectangleGeometry(new Rect(0, 0, SceneWidth, 575));
            scene.Children.Add(blood);

            settingsPanel = new Border { Width = 468, Height = 359, CornerRadius = new CornerRadius(14), Background = Brush("#FA17191B"),
                BorderBrush = Brush("#66605B55"), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
            var panel = new Canvas(); settingsPanel.Child = panel; Place(settingsPanel, 234, 205, scene);
            panelTitle = TextAt("Настройки", 25, 20, 365, 30, 20, panel);
            var dismiss = ButtonAt("×", 408, 14, 38, 36, panel); dismiss.FontSize = 21;
            dismiss.Padding = new Thickness(0);
            AutomationProperties.SetName(dismiss, "Закрыть настройки"); dismiss.Click += delegate { HideSettings(); };
            settingsContent = new StackPanel { Width = 414 }; Place(settingsContent, 26, 67, panel);
            folder = new TextBlock { Foreground = Brush("#D8D4CD"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Height = 23 };
            settingsContent.Children.Add(folder);
            var firstRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 13) };
            settingsContent.Children.Add(firstRow);
            browse = SmallButton("Папка игры", firstRow); retry = SmallButton("Проверить", firstRow);
            var details = SmallButton("Подробности", firstRow);
            version = new TextBlock { Foreground = Brush("#9F9F9C"), FontSize = 12, LineHeight = 20, Margin = new Thickness(0, 0, 0, 10) };
            settingsContent.Children.Add(version);
            var effectsBox = new CheckBox { Content = "Анимация и эффекты", Foreground = Brush("#D8D4CD"), IsChecked = true, Margin = new Thickness(0, 0, 0, 13) };
            settingsContent.Children.Add(effectsBox);
            effectsBox.Checked += delegate { effects = true; SetAtmosphere(busy ? "updating" : "idle", false); play.Cursor = Cursors.Hand; };
            effectsBox.Unchecked += delegate { effects = false; SetAtmosphere("idle", true); blood.Children.Clear(); play.Cursor = Cursors.Hand; };
            var secondRow = new StackPanel { Orientation = Orientation.Horizontal }; settingsContent.Children.Add(secondRow);
            var releases = SmallButton("Что нового ↗", secondRow); remove = SmallButton("Удалить сборку", secondRow);
            var lastRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) }; settingsContent.Children.Add(lastRow);
            var minimize = SmallButton("Свернуть", lastRow); var exit = SmallButton("Выход", lastRow);
            detailsText = new TextBox { Width = 414, Height = 263, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = Brush("#D8D4CD"), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Visibility = Visibility.Collapsed };
            Place(detailsText, 26, 67, panel);

            play.Click += delegate { Play(); };
            update.Click += delegate { UpdateMods(); };
            gear.Click += delegate { if (settingsPanel.Visibility == Visibility.Visible) HideSettings(); else ShowSettings(); };
            browse.Click += delegate { Browse(); }; retry.Click += delegate { CheckRelease(); };
            details.Click += delegate { ShowDetails(); }; remove.Click += delegate { RemoveMods(); };
            releases.Click += delegate {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleaseRoot) { UseShellExecute = true }); }
                catch (Exception error) { SetError(error); }
            };
            minimize.Click += delegate { WindowState = WindowState.Minimized; }; exit.Click += delegate { Close(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { HideSettings(); e.Handled = true; } };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (busy || launching) { e.Cancel = true; Notify("Дождитесь завершения операции"); } };
            Closed += delegate { Dispose(); };
            if (preview) SetPreviewState("ready");
            else { gameDir = SavedGame(); RenderState(); Loaded += delegate { CheckRelease(); }; }
        }

        static SolidColorBrush Brush(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); brush.Freeze(); return brush; }
        readonly byte[][] zombiePoses = new byte[8][];
        readonly byte[] zombiePixels = new byte[128 * 256 * 4];
        WriteableBitmap zombieBitmap;
        DispatcherTimer zombieTimer;
        readonly System.Diagnostics.Stopwatch zombieClock = new System.Diagnostics.Stopwatch();
        int zombieFrame;

        void AddZombie()
        {
            BitmapSource sheet;
            using (var stream = OpenResource("ZombieIdle.png"))
                sheet = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            for (int i = 0; i < 8; i++) {
                var cell = new CroppedBitmap(sheet, new Int32Rect(i % 4 * (sheet.PixelWidth / 4), i / 4 * (sheet.PixelHeight / 2), sheet.PixelWidth / 4, sheet.PixelHeight / 2));
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawImage(cell, new Rect(-32, 0, 192, 256));
                var pose = new RenderTargetBitmap(128, 256, 96, 96, PixelFormats.Pbgra32);
                pose.Render(visual); zombiePoses[i] = new byte[zombiePixels.Length];
                pose.CopyPixels(zombiePoses[i], 128 * 4, 0);
            }
            var layer = new Canvas { IsHitTestVisible = false };
            var shadowBrush = new RadialGradientBrush(Color.FromArgb(145, 0, 0, 0), Colors.Transparent);
            Place(new Ellipse { Width = 49, Height = 8, Fill = shadowBrush }, 218, 494, layer);
            zombieBitmap = new WriteableBitmap(128, 256, 96, 96, PixelFormats.Pbgra32, null);
            var zombie = new Image { Source = zombieBitmap, Width = 64, Height = 128, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(zombie, BitmapScalingMode.HighQuality);
            Place(zombie, 208, 374, layer);
            // Character and contact shadow remain inside the existing silhouette,
            // underneath the moonlight overlays and all interactive controls.
            atmosphere.Children.Add(layer);
            DrawZombie(0);
            zombieTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            zombieTimer.Tick += delegate {
                if (!disposed && effects && WindowState != WindowState.Minimized)
                    DrawZombie((int)(zombieClock.ElapsedMilliseconds / 20 % 400));
            };
            zombieClock.Start(); zombieTimer.Start();
        }

        void DrawZombie(int index)
        {
            int[] durations = { 72, 72, 72, 72, 72, 4, 8, 28 };
            zombieFrame = index;
            int pose = 0;
            while (index >= durations[pose]) { index -= durations[pose]; pose++; }
            double t = index / (double)durations[pose]; t = t * t * (3 - 2 * t);
            byte[] first = zombiePoses[pose], next = zombiePoses[(pose + 1) % 8];
            for (int p = 0; p < zombiePixels.Length; p++)
                zombiePixels[p] = (byte)Math.Round(first[p] * (1 - t) + next[p] * t);
            zombieBitmap.WritePixels(new Int32Rect(0, 0, 128, 256), zombiePixels, 128 * 4, 0);
        }
        static void Place(UIElement child, double x, double y, Canvas parent) { Canvas.SetLeft(child, x); Canvas.SetTop(child, y); parent.Children.Add(child); }
        Button ButtonAt(string caption, double x, double y, double width, double height, Canvas parent)
        {
            var button = new Button { Content = caption, Style = buttonStyle, Width = width, Height = height };
            AutomationProperties.SetName(button, caption); Place(button, x, y, parent); return button;
        }
        void MakeRustyGear(Button button, string resource = "RustyGear.png")
        {
            var art = new BitmapImage();
            using (var stream = OpenResource(resource)) {
                art.BeginInit(); art.CacheOption = BitmapCacheOption.OnLoad;
                art.StreamSource = stream; art.EndInit(); art.Freeze();
            }
            var image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(Image.SourceProperty, art);
            image.SetValue(Image.StretchProperty, Stretch.Uniform);
            image.SetValue(UIElement.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect {
                Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = .18
            });
            var template = new ControlTemplate(typeof(Button)) { VisualTree = image };
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, .6)); template.Triggers.Add(pressed);
            button.Template = template; button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.MouseEnter += delegate { Fade(button, 1, .2); };
            button.MouseLeave += delegate { Fade(button, .87, .3); };
            button.Opacity = .87;
            AddHoverScale(button, 1.08);
        }
        void AddHoverScale(Button button, double amount)
        {
            var scale = new ScaleTransform(1, 1);
            button.RenderTransformOrigin = new Point(.5, .5);
            button.RenderTransform = scale;
            Action<double> animate = delegate(double target) {
                var animation = new DoubleAnimation(target, TimeSpan.FromSeconds(effects ? .18 : 0)) {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            };
            button.MouseEnter += delegate { if (button.IsEnabled) animate(amount); };
            button.MouseLeave += delegate { animate(1); };
            button.IsEnabledChanged += delegate { if (!button.IsEnabled) animate(1); };
        }
        void MakePlayMark(Button button)
        {
            BitmapImage mark = new BitmapImage();
            using (var stream = OpenResource("SevenDaysButton.png")) {
                mark.BeginInit(); mark.CacheOption = BitmapCacheOption.OnLoad;
                mark.StreamSource = stream; mark.EndInit(); mark.Freeze();
            }
            BitmapImage idleMark = new BitmapImage();
            using (var stream = OpenResource("SevenDaysButton-Idle.png")) {
                idleMark.BeginInit(); idleMark.CacheOption = BitmapCacheOption.OnLoad;
                idleMark.StreamSource = stream; idleMark.EndInit(); idleMark.Freeze();
            }
            var layers = new FrameworkElementFactory(typeof(Grid));
            layers.SetValue(UIElement.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1.5, Opacity = .45
            });
            var image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(Image.SourceProperty, idleMark);
            image.SetValue(Image.StretchProperty, Stretch.Uniform);
            layers.AppendChild(image);
            var wet = new FrameworkElementFactory(typeof(Image), "FreshBlood");
            wet.SetValue(Image.SourceProperty, mark); wet.SetValue(Image.StretchProperty, Stretch.Uniform);
            wet.SetValue(OpacityProperty, 0.0); layers.AppendChild(wet);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = layers };
            button.MouseEnter += delegate {
                var overlay = button.Template.FindName("FreshBlood", button) as Image;
                if (overlay != null) Fade(overlay, 1, effects ? .3 : 0);
            };
            button.MouseLeave += delegate {
                var overlay = button.Template.FindName("FreshBlood", button) as Image;
                if (overlay != null) Fade(overlay, 0, effects ? .45 : 0);
            };
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, .55)); template.Triggers.Add(pressed);
            button.Template = template; button.Width = 197.5; button.Height = 75;
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            button.ToolTip = null;
            Canvas.SetLeft(button, 697); Canvas.SetTop(button, 485);
            AddHoverScale(button, 1.05);
        }
        Button SmallButton(string caption, Panel parent)
        {
            var button = new Button { Content = caption, Style = buttonStyle, FontSize = 12, Height = 34, Padding = new Thickness(11, 3, 11, 3), Margin = new Thickness(0, 0, 7, 0) };
            parent.Children.Add(button); return button;
        }
        static TextBlock TextAt(string text, double x, double y, double width, double height, double size, Canvas parent)
        {
            var block = new TextBlock { Text = text, Width = width, Height = height, FontSize = size, FontFamily = new FontFamily("Segoe UI"), Foreground = Brush("#ECE4DA") };
            Place(block, x, y, parent); return block;
        }
        static BitmapSource OpaqueColor(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = source.PixelWidth * 4; byte[] pixels = new byte[stride * source.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); result.Freeze(); return result;
        }
        static Rectangle Glow(string color, Point center, double radius)
        {
            var brush = new RadialGradientBrush((Color)ColorConverter.ConvertFromString(color), Colors.Transparent) { Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius };
            brush.Freeze(); return new Rectangle { Fill = brush, Opacity = 0, IsHitTestVisible = false };
        }
        static void Fade(UIElement element, double opacity, double seconds)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromSeconds(seconds)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        void SetAtmosphere(string state, bool immediate)
        {
            DrawBloodProgress();
            bool bloodMoon = effects && state == "launch";
            double warm = effects ? .28 : 0;
            double red = bloodMoon ? .20 : 0, spill = bloodMoon ? .34 : 0;
            if (immediate) {
                warmLight.BeginAnimation(OpacityProperty, null); warmLight.Opacity = warm;
                redLight.BeginAnimation(OpacityProperty, null); redLight.Opacity = red;
                redScene.BeginAnimation(OpacityProperty, null); redScene.Opacity = bloodMoon ? 1 : 0;
                redSpill.BeginAnimation(OpacityProperty, null); redSpill.Opacity = spill;
            } else {
                warmLight.BeginAnimation(OpacityProperty, null); warmLight.Opacity = warm;
                Fade(redScene, bloodMoon ? 1 : 0, bloodMoon ? 2.4 : 1.2);
                Fade(redLight, red, bloodMoon ? 2.2 : 1.2);
                // The reflected light arrives just after the moon changes color.
                redSpill.BeginAnimation(OpacityProperty, new DoubleAnimation(spill, TimeSpan.FromSeconds(bloodMoon ? 2.4 : 1.2)) {
                    BeginTime = TimeSpan.FromSeconds(bloodMoon ? .2 : 0),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            Fade(coolShade, bloodMoon ? 0 : .035, .9);
            emberLayer.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;
            if (!effects && lampShade != null) lampShade.Opacity = 0;
        }
        Canvas emberLayer;
        void DrawBloodProgress()
        {
            if (bloodProgress == null) return;
            bloodProgress.Visibility = bloodProgressTrack.Visibility = progress.Visibility;
            if (progressNumber != null) {
                progressNumber.Visibility = progress.Visibility == Visibility.Visible && !progress.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
                progressNumber.Text = Math.Round(progress.Value).ToString() + "%";
            }
            double width = 520 * Math.Max(0, Math.Min(100, progress.Value)) / 100;
            bloodProgressClip.Rect = new Rect(0, 0, width, 18);
        }
        sealed class Ember
        {
            public Ellipse Dot;
            public double X, Y, Age, Life, Speed, Drift, Phase;
        }
        readonly List<Ember> embers = new List<Ember>();
        DispatcherTimer atmosphereTimer;
        Image lampShade;
        double lampCountdown = .8;
        int lampSwitches;
        bool lampOff = true;
        void ResetEmber(Ember ember, bool initial)
        {
            ember.X = 390 + random.NextDouble() * 410;
            ember.Y = 435 + random.NextDouble() * 60;
            ember.Life = 2.8 + random.NextDouble() * 4.5;
            ember.Age = initial ? random.NextDouble() * ember.Life : -random.NextDouble() * 2;
            ember.Speed = 13 + random.NextDouble() * 25;
            ember.Drift = -14 + random.NextDouble() * 28;
            ember.Phase = random.NextDouble() * Math.PI * 2;
            ember.Dot.Width = ember.Dot.Height = .9 + random.NextDouble() * 1.8;
        }
        void AddEmbers()
        {

            emberLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            atmosphere.Children.Add(emberLayer);
            for (int i = 0; i < 13; i++) {
                var ember = new Ember { Dot = new Ellipse { Fill = Brush(i % 3 == 0 ? "#D47735" : "#EFB46E"), Opacity = 0 } };
                ResetEmber(ember, true); emberLayer.Children.Add(ember.Dot); embers.Add(ember);
            }
            atmosphereTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            atmosphereTimer.Tick += delegate {
                if (disposed || WindowState == WindowState.Minimized) return;
                if (!effects) { lampOff = true; lampSwitches = 0; lampCountdown = .8; lampShade.Opacity = 0; return; }
                foreach (var ember in embers) {
                    ember.Age += .04;
                    if (ember.Age > ember.Life) ResetEmber(ember, false);
                    double age = Math.Max(0, ember.Age), fraction = age / ember.Life;
                    Canvas.SetLeft(ember.Dot, ember.X + age * ember.Drift + Math.Sin(age * 1.6 + ember.Phase) * 9);
                    Canvas.SetTop(ember.Dot, ember.Y - age * ember.Speed);
                    ember.Dot.Opacity = ember.Age < 0 ? 0 : Math.Min(1, age * 3) * (1 - fraction) * (.45 + .2 * Math.Sin(age * 5 + ember.Phase));
                }
                lampCountdown -= .04;
                if (lampCountdown <= 0) {
                    if (lampSwitches == 0) lampSwitches = random.Next(2, 5) * 2;
                    lampOff = !lampOff; lampSwitches--;
                    lampCountdown = lampSwitches == 0 ? .7 + random.NextDouble() * 1.3
                        : lampOff ? .12 + random.NextDouble() * .25 : .04 + random.NextDouble() * .10;
                    // Discrete changes: a faulty bulb, with no fade between levels.

                    lampShade.Opacity = lampOff ? 1 : 0;
                }
            };
            atmosphereTimer.Start();
        }
        void ShowSettings()
        {
            panelTitle.Text = "Настройки"; settingsContent.Visibility = Visibility.Visible; detailsText.Visibility = Visibility.Collapsed;
            settingsPanel.Visibility = Visibility.Visible; browse.Focus();
        }
        void HideSettings() { settingsPanel.Visibility = Visibility.Collapsed; }
        void ShowDetails()
        {
            panelTitle.Text = lastError == null ? "Состав сборки" : "Ошибка";
            detailsText.Text = lastError ?? (remote == null ? "Проверка ещё не завершена." : remote.Version + "\n\n" + String.Join("\n", remote.Packages.Select(p => Path.GetFileNameWithoutExtension(p.Name))));
            settingsContent.Visibility = Visibility.Collapsed; detailsText.Visibility = Visibility.Visible; settingsPanel.Visibility = Visibility.Visible;
        }
        void Delay(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timers.Add(timer);
            timer.Tick += delegate { timer.Stop(); timers.Remove(timer); if (!disposed) action(); }; timer.Start();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var timer in timers) timer.Stop(); timers.Clear();
            if (zombieTimer != null) zombieTimer.Stop(); zombieClock.Stop();
            if (atmosphereTimer != null) atmosphereTimer.Stop();
            if (IsVisible) { busy = launching = false; Close(); }
        }
    }
}























