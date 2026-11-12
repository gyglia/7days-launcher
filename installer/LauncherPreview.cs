using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

partial class Installer
{
    sealed partial class LauncherWindow
    {
        public void SetPreviewState(string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview only");
            foreach (var timer in timers) timer.Stop(); timers.Clear(); blood.Children.Clear();
            HideSettings();
            remote = bundled; gameDir = @"D:\SteamLibrary\steamapps\common\7 Days To Die";
            installed = new Receipt { Release = bundled.Version, Names = bundled.Packages.SelectMany(p => p.Folders).ToArray() };
            previewChanges = new ModPackage[0]; previewGameValid = true;
            busy = launching = offline = false; lastError = null;
            if (state == "update") { installed.Release = "sever2026.0"; previewChanges = bundled.Packages; }
            else if (state == "install" || state == "missing") { installed = null; previewChanges = bundled.Packages; }
            else if (!new[] { "ready", "offline", "busy", "launch", "error", "settings", "details", "lampoff" }.Contains(state)) throw new ArgumentException("Unknown preview state: " + state);
            if (state == "missing") { previewGameValid = false; gameDir = ""; }
            offline = state == "offline";
            RenderState(); SetAtmosphere("idle", true);
            if (state == "busy") {
                busy = true; RenderState(); status.Text = "";
                progress.IsIndeterminate = false; progress.Value = 42; progress.Visibility = Visibility.Visible;
                SetAtmosphere("updating", true);
            }
            if (state == "launch") { launching = true; RenderState(); SetAtmosphere("launch", true); }
            if (state == "error") SetError(new IOException("Не удалось скачать пакет. Проверьте подключение к интернету и повторите проверку."));
            if (state == "settings") ShowSettings();
            if (state == "details") ShowDetails();
            if (state == "lampoff") { atmosphereTimer.Stop(); lampShade.Opacity = 1; }
        }

        void StartDemoUpdate()
        {
            if (!previewMode || busy || launching) return;
            HideSettings(); busy = true; RenderState(); SetAtmosphere("updating", false);
            progress.IsIndeterminate = false; progress.Value = 0; progress.Visibility = Visibility.Visible;
            int percent = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) }; timers.Add(timer);
            timer.Tick += delegate {
                percent += 3; progress.Value = Math.Min(100, percent); status.Text = "";
                if (percent >= 100) { timer.Stop(); timers.Remove(timer); busy = false; RenderState(); }
            }; timer.Start();
        }

        static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }

        BitmapSource Snapshot(bool matte)
        {
            scene.Measure(new Size(SceneWidth, SceneHeight)); scene.Arrange(new Rect(0, 0, SceneWidth, SceneHeight)); scene.UpdateLayout();
            var result = new RenderTargetBitmap(SceneWidth, SceneHeight, 96, 96, PixelFormats.Pbgra32);
            if (matte) {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) {
                    dc.DrawRectangle(Brush("#292D32"), null, new Rect(0, 0, SceneWidth, SceneHeight));
                    dc.DrawRectangle(new VisualBrush(scene), null, new Rect(0, 0, SceneWidth, SceneHeight));
                }
                result.Render(visual);
            } else result.Render(scene);
            result.Freeze(); return result;
        }

        public void SavePreview(string file)
        {
            Pump(280);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Snapshot(false)));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }

        public void ExportAnimation(string directory)
        {
            Directory.CreateDirectory(directory);
            SetPreviewState("ready");
            scene.Measure(new Size(SceneWidth, SceneHeight)); scene.Arrange(new Rect(0, 0, SceneWidth, SceneHeight));
            var encoder = new GifBitmapEncoder();
            for (int i = 0; i < 100; i++) {
                if (i == 82) Play();
                Pump(100);
                var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)10); metadata.SetQuery("/grctlext/Disposal", (byte)2);
                if (i == 0) { metadata.SetQuery("/appext/application", Encoding.ASCII.GetBytes("NETSCAPE2.0")); metadata.SetQuery("/appext/data", new byte[] { 3, 1, 0, 0, 0 }); }
                encoder.Frames.Add(BitmapFrame.Create(Snapshot(true), null, metadata, null));
            }
            SaveAnimatedGif(encoder, Path.Combine(directory, "launcher-motion.gif"));
        }

        static void SaveAnimatedGif(GifBitmapEncoder encoder, string file)
        {
            // WIC can omit frame delay and loop metadata. Write standard GIF blocks
            // explicitly so browsers and GDI+ play the same timed animation.
            byte[] bytes;
            using (var memory = new MemoryStream()) { encoder.Save(memory); bytes = memory.ToArray(); }
            int paletteEnd = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
            int cursor = paletteEnd;
            while (cursor < bytes.Length) {
                byte marker = bytes[cursor++];
                if (marker == 0x3b) break;
                if (marker == 0x21) {
                    byte label = bytes[cursor++];
                    if (label == 0xf9 && bytes[cursor] == 4) { bytes[cursor + 1] = (byte)((bytes[cursor + 1] & 1) | 8); bytes[cursor + 2] = 10; bytes[cursor + 3] = 0; }
                    while (bytes[cursor] != 0) cursor += bytes[cursor] + 1;
                    cursor++;
                } else if (marker == 0x2c) {
                    byte flags = bytes[cursor + 8]; cursor += 9;
                    if ((flags & 128) != 0) cursor += 3 * (1 << ((flags & 7) + 1));
                    cursor++;
                    while (bytes[cursor] != 0) cursor += bytes[cursor] + 1;
                    cursor++;
                } else throw new IOException("Invalid generated GIF block");
            }
            using (var stream = File.Create(file)) {
                stream.Write(bytes, 0, paletteEnd);
                byte[] loop = { 0x21, 0xff, 11, 78, 69, 84, 83, 67, 65, 80, 69, 50, 46, 48, 3, 1, 0, 0, 0 };
                stream.Write(loop, 0, loop.Length);
                stream.Write(bytes, paletteEnd, bytes.Length - paletteEnd);
            }
        }

        public int TestInterface(string log)
        {
            try {
                SetPreviewState("ready"); Require(play.IsEnabled && update.IsEnabled, "Ready actions"); Require(play.ToolTip == null, "Play has no tooltip");
                Require(chooseArchive.IsEnabled && !applyArchive.IsEnabled, "Archive required before manual update");
                manualArchive = "test.zip"; RenderState(); Require(applyArchive.IsEnabled,"Selected archive enables confirmation");
                SetPreviewState("busy"); Require(!chooseArchive.IsEnabled && !applyArchive.IsEnabled,"Manual update blocked while busy");
                manualArchive = null; SetPreviewState("ready");
                Require(scene.Children.OfType<System.Windows.Controls.Button>().Count(b => b.Visibility == Visibility.Visible) == 2, "Only play and settings visible");
                int initialZombieFrame = zombieFrame; Pump(100);
                Require(zombieFrame != initialZombieFrame, "Zombie animation advances");
                effects = false; initialZombieFrame = zombieFrame; Pump(100);
                Require(zombieFrame == initialZombieFrame, "Effects setting pauses zombie animation");
                effects = true;
                var image = Snapshot(false); byte[] pixels = new byte[SceneWidth * SceneHeight * 4]; image.CopyPixels(pixels, SceneWidth * 4, 0);
                Require(pixels[3] == 0 && pixels[(SceneWidth - 1) * 4 + 3] == 0, "Transparent outer corners");
                int moonPixel = (100 * SceneWidth + 100) * 4;
                Require(pixels[moonPixel + 2] > 80 && pixels[moonPixel] >= pixels[moonPixel + 2] * .85, "White moon in idle state");
                SetPreviewState("launch");
                byte[] redPixels = new byte[pixels.Length]; Snapshot(false).CopyPixels(redPixels, SceneWidth * 4, 0);
                Require(redPixels[moonPixel + 2] > redPixels[moonPixel + 1] * 1.5, "Moon becomes red at launch");
                int roofPixel = (280 * SceneWidth + 650) * 4;
                Require(redPixels[roofPixel + 2] > redPixels[roofPixel + 1] * 1.3, "Red moonlight reaches the house");
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] == 0) Require(redPixels[i] == 0, "Launch preserves transparent silhouette");
                Require(Title.IndexOf("JAGO", StringComparison.OrdinalIgnoreCase) < 0, "No branding in title");
                SetPreviewState("update"); Require(!play.IsEnabled && update.IsEnabled, "Update required");
                SetPreviewState("missing"); Require(!play.IsEnabled && update.IsEnabled && !HasGame(), "Missing game path");
                SetPreviewState("offline"); Require(play.IsEnabled, "Offline saved build playable");
                SetPreviewState("busy"); Require(!play.IsEnabled && !update.IsEnabled, "No concurrent action while busy");
                SetPreviewState("error"); ShowDetails(); Require(detailsText.Text.Contains("скачать"), "Error details accessible");
                SetPreviewState("ready"); Play(); Require(launching && !play.IsEnabled && blood.Children.Count == 0, "Launch guard without blood particles");
                Pump(3100); Require(!launching && play.IsEnabled, "Demo launch completed without starting game");
                File.WriteAllText(log, "PASS: white idle moon; red launch moon and reflected light; invariant transparent silhouette; 3 controls; ready/update/missing/offline/busy/error states; no knife or blood particles; launch guard; demo completion without game execution.");
                return 0;
            } catch (Exception error) { File.WriteAllText(log, error.ToString()); return 1; }
        }
    }
}







