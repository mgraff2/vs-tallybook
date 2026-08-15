using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>
    /// Walks the mod's own screens taking one screenshot each, into stable feature-named
    /// files — so "refresh the ModDB screenshots" is one command in your real world rather
    /// than a manual tour with the print-screen key and a renaming session afterwards.
    /// The manifest in docs/moddb-screenshots.md says what each shot is meant to show and
    /// how to stage it; this only takes them.
    ///
    /// Two rules it keeps, both deliberate:
    ///
    /// - **It navigates, never edits.** Setups open a screen and select a tab; nothing here
    ///   pins, unpins, expands, or changes a count. A showcase run cannot alter the list it
    ///   is photographing, which is the same "only ever reads" promise the rest of the mod
    ///   makes — and it means an honest screenshot of your real world, not a staged one.
    /// - **A blank grab is reported, never saved quietly.** Which framebuffer is bound at a
    ///   given render stage is the one thing here that cannot be verified from the API
    ///   surface (the game binds one explicitly before its own capture, using platform
    ///   internals a mod has no access to). So every grab is checked for being a single flat
    ///   colour, and a blank run says so with the exact command to retry at the other stage
    ///   rather than leaving a folder of black PNGs that look like a rendering bug.
    /// </summary>
    public class ShowcaseShots : IRenderer
    {
        /// <summary>A window's on-screen rectangle in real pixels, top-left origin — what a
        /// shot gets cropped to.</summary>
        public class Rect
        {
            public int X, Y, W, H;
        }

        public class Shot
        {
            /// <summary>Stable file name (no extension) — ModDB replacement is mechanical
            /// only if the same feature keeps the same name across releases.</summary>
            public string Name;
            /// <summary>What it should show, echoed in chat as the run passes through.</summary>
            public string What;
            /// <summary>Puts the game in the state to photograph. Returns false when the
            /// view is unavailable (a tab switched off), which skips the shot rather than
            /// photographing whatever happened to be on screen under the wrong name.</summary>
            public Func<bool> Setup;
            /// <summary>The window to crop to, asked for at capture time (bounds are only
            /// final once the composer has rebuilt). Null, or a null return, keeps the
            /// whole screen.</summary>
            public Func<Rect> Window;
            /// <summary>Exact output size in pixels, or 0 for "as big as the window is".
            /// A fixed size crops a region of that size centred on the window — so the
            /// picture is the same shape every release however many rows the window has
            /// that day, with game visible around it rather than the window stretched.</summary>
            public int TargetW, TargetH;
            /// <summary>This shot is of the world, not a window — keep the whole screen
            /// (still held inside the size ceiling) and do not count it as a failed crop.</summary>
            public bool FullScreen;
            /// <summary>Said in chat when the setup declines the shot, because "unavailable"
            /// means something different per shot — a tab switched off, or nothing in the
            /// world to photograph yet.</summary>
            public string SkipHint;
        }

        readonly ICoreClientAPI capi;

        List<Shot> queue;
        int index;
        bool running;
        Shot pending;
        bool flip;
        EnumRenderStage stage;
        int pad;
        bool crop;
        List<string> taken, skipped, blank, uncropped;

        /// <summary>Time for a composer to rebuild and the replaced one to be disposed
        /// (that dispose is on a 250ms timer), plus a beat for anything animating.</summary>
        const int SettleMs = 700;

        public ShowcaseShots(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public double RenderOrder => 1.0;
        public int RenderRange => 0;

        public bool Running => running;

        /// <summary>
        /// Where the shots land. Deliberately NOT `GamePaths.Screenshots` — that resolves to
        /// the user's Pictures folder, which on Windows is commonly redirected into OneDrive
        /// (found by Mark: the first run filed them into his personal cloud pictures). These
        /// are working files for a mod page, not the player's own screenshots, so they go
        /// with the mod's other outputs under VintagestoryData/ModData/tallybook/.
        /// </summary>
        public static string Folder
            => Path.Combine(GamePaths.DataPath, "ModData", "tallybook", "screenshots");

        public string Start(List<Shot> shots, int preRollMs, bool flipImage, EnumRenderStage renderStage,
                            int padPx, bool cropToWindow)
        {
            if (running) return "A showcase run is already going — let it finish.";
            if (shots == null || shots.Count == 0) return "Nothing to photograph.";

            try { Directory.CreateDirectory(Folder); }
            catch (Exception e) { return $"Could not create {Folder}: {e.Message}"; }

            queue = shots;
            index = -1;
            flip = flipImage;
            stage = renderStage;
            pad = Math.Max(0, padPx);
            crop = cropToWindow;
            taken = new List<string>();
            skipped = new List<string>();
            blank = new List<string>();
            uncropped = new List<string>();
            running = true;

            capi.Event.RegisterRenderer(this, stage, "tallybook-showcase");
            // The pre-roll is the player's window to get the chat log to fade and the mouse
            // out of the way — both would otherwise be in every shot.
            Schedule(Step, Math.Max(200, preRollMs));
            return null;
        }

        void Schedule(Action action, int ms)
        {
            // 3-arg overload throughout: our own dialog does not pause the game, but the
            // handbook does and stays interactive, so a run started from there would
            // otherwise trip the paused-callback trap.
            capi.Event.RegisterCallback(_ =>
            {
                try { action(); }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] showcase step failed: {0}", e.Message);
                    Finish();
                }
            }, ms, permittedWhilePaused: true);
        }

        void Step()
        {
            if (!running) return;
            index++;
            if (index >= queue.Count) { Finish(); return; }

            var shot = queue[index];
            bool ready;
            try { ready = shot.Setup?.Invoke() ?? true; }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] showcase setup for {0} failed: {1}", shot.Name, e.Message);
                ready = false;
            }

            if (!ready)
            {
                skipped.Add(shot.Name);
                if (shot.SkipHint != null)
                    capi.ShowChatMessage($"[tallybook] skipped {shot.Name}: {shot.SkipHint}");
                Schedule(Step, 50);
                return;
            }
            Schedule(() => pending = shot, SettleMs);
        }

        public void OnRenderFrame(float dt, EnumRenderStage renderStage)
        {
            var shot = pending;
            if (shot == null) return;
            pending = null;

            try
            {
                int frameW = capi.Render.FrameWidth, frameH = capi.Render.FrameHeight;
                using var bitmap = capi.Render.GrabScreenshot(
                    frameW, frameH, scaleScreenshot: false, flip: flip, withAlpha: false);

                if (bitmap == null) { blank.Add(shot.Name); }
                else
                {
                    if (IsFlat(bitmap)) blank.Add(shot.Name);

                    string path = Path.Combine(Folder, shot.Name + ".png");
                    // Saved by the game's own writer first, then cropped by re-reading the
                    // file: the alternative — cropping BitmapRef.Pixels by hand — would put
                    // this code in the business of guessing the buffer's channel order, and
                    // getting that wrong swaps red and blue in every shot.
                    bitmap.Save(path);
                    taken.Add(shot.Name);

                    // Runs on every shot, cropping or not: the size ceiling applies to a
                    // full-screen grab as much as to a window.
                    bool wantWindow = crop && !shot.FullScreen;
                    var window = wantWindow ? SafeWindow(shot) : null;
                    bool ok = ProcessInPlace(path, window, frameW, frameH, shot);
                    if (wantWindow && (window == null || !ok)) uncropped.Add(shot.Name);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] showcase grab for {0} failed: {1}", shot.Name, e.Message);
                blank.Add(shot.Name);
            }

            // Back to the walker on the main thread — never do the next setup from inside
            // a render callback.
            Schedule(Step, 150);
        }

        Rect SafeWindow(Shot shot)
        {
            try { return shot.Window?.Invoke(); }
            catch { return null; }
        }

        /// <summary>The mod DB's upload ceiling — nothing leaves here bigger (Mark). A 4K
        /// screen or a supersampled framebuffer sails past this without it.</summary>
        public const int MaxW = 1920, MaxH = 1080;

        /// <summary>
        /// Crop the written PNG to one window and hold it inside the size ceiling, in place.
        ///
        /// The window rectangle arrives in GUI pixels while the image can legitimately be a
        /// different size (SSAA), so it is scaled by the ratio rather than assumed to match —
        /// an unscaled rect crops the wrong part of the picture on any machine with
        /// supersampling on. Returns false when a requested crop could not be done, so the
        /// run can say the file is a full screen rather than let it pass as a window shot.
        ///
        /// With a target size the output is exactly that size: normally by taking a region of
        /// exactly those pixels centred on the window — no resampling at all, the window
        /// sitting in the middle with game around it — and scaling only when the window is
        /// too big to fit, which keeps the whole window in frame rather than cutting it off.
        /// </summary>
        bool ProcessInPlace(string path, Rect window, int frameW, int frameH, Shot shot)
        {
            try
            {
                using var full = SKBitmap.Decode(path);
                if (full == null || full.Width <= 0 || full.Height <= 0) return false;

                int targetW = shot?.TargetW ?? 0, targetH = shot?.TargetH ?? 0;
                int left = 0, top = 0, right = full.Width, bottom = full.Height;

                if (window != null)
                {
                    double sx = frameW > 0 ? (double)full.Width / frameW : 1;
                    double sy = frameH > 0 ? (double)full.Height / frameH : 1;

                    // The window in image pixels, with the margin already applied.
                    int wx = (int)Math.Floor((window.X - pad) * sx);
                    int wy = (int)Math.Floor((window.Y - pad) * sy);
                    int ww = (int)Math.Ceiling((window.W + pad * 2) * sx);
                    int wh = (int)Math.Ceiling((window.H + pad * 2) * sy);

                    if (targetW > 0 && targetH > 0)
                    {
                        // Grow the region only if the window would not fit, and then in the
                        // target's own proportions so the eventual scale-down keeps its shape.
                        int regW = targetW, regH = targetH;
                        double grow = Math.Max((double)ww / targetW, (double)wh / targetH);
                        if (grow > 1)
                        {
                            regW = (int)Math.Ceiling(targetW * grow);
                            regH = (int)Math.Ceiling(targetH * grow);
                        }
                        regW = Math.Min(regW, full.Width);
                        regH = Math.Min(regH, full.Height);

                        // Centred on the window, then slid back inside the picture — a window
                        // in a screen corner (which the HUD always is) would otherwise take
                        // half its region from outside the image and come back short.
                        left = Math.Clamp(wx + ww / 2 - regW / 2, 0, full.Width - regW);
                        top = Math.Clamp(wy + wh / 2 - regH / 2, 0, full.Height - regH);
                        right = left + regW;
                        bottom = top + regH;
                    }
                    else
                    {
                        left = Math.Clamp(wx, 0, full.Width - 1);
                        top = Math.Clamp(wy, 0, full.Height - 1);
                        right = Math.Clamp(wx + ww, left + 1, full.Width);
                        bottom = Math.Clamp(wy + wh, top + 1, full.Height);
                    }
                    if (right - left < 16 || bottom - top < 16) return false;
                }

                int regionW = right - left, regionH = bottom - top;

                // What the file should end up as: the fixed size when one is asked for,
                // otherwise the region itself — either way held inside the ceiling.
                int outW = targetW > 0 ? targetW : regionW;
                int outH = targetH > 0 ? targetH : regionH;
                double shrink = Math.Min(1.0, Math.Min((double)MaxW / outW, (double)MaxH / outH));
                if (shrink < 1)
                {
                    outW = Math.Max(1, (int)Math.Round(outW * shrink));
                    outH = Math.Max(1, (int)Math.Round(outH * shrink));
                }

                bool cropping = regionW != full.Width || regionH != full.Height;
                bool resizing = outW != regionW || outH != regionH;
                if (!cropping && !resizing) return true;   // already right; leave the file alone

                SKBitmap subset = null, scaled = null;
                try
                {
                    var final = full;
                    if (cropping)
                    {
                        subset = new SKBitmap();
                        if (!full.ExtractSubset(subset, new SKRectI(left, top, right, bottom))) return false;
                        final = subset;
                    }
                    if (resizing)
                    {
                        scaled = final.Resize(new SKImageInfo(outW, outH),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        if (scaled == null) return false;
                        final = scaled;
                    }

                    // To memory first: the source file is still open for decoding, and on
                    // Windows writing over it while mapped fails.
                    using var data = final.Encode(SKEncodedImageFormat.Png, 100);
                    if (data == null) return false;
                    byte[] bytes = data.ToArray();

                    full.Dispose();
                    File.WriteAllBytes(path, bytes);
                    return true;
                }
                finally { scaled?.Dispose(); subset?.Dispose(); }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not process {0}: {1}", path, e.Message);
                return false;
            }
        }

        /// <summary>Is every sampled pixel the same colour? That is what a grab of an
        /// unbound or already-cleared framebuffer looks like, and it is indistinguishable
        /// from a working capture until someone opens the file.</summary>
        static bool IsFlat(Vintagestory.API.Common.BitmapRef bitmap)
        {
            try
            {
                var pixels = bitmap.Pixels;
                if (pixels == null || pixels.Length == 0) return true;

                int first = pixels[0];
                int step = Math.Max(1, pixels.Length / 512);
                for (int i = step; i < pixels.Length; i += step)
                {
                    if (pixels[i] != first) return false;
                }
                return true;
            }
            catch { return false; }   // unreadable pixels are not evidence of a blank shot
        }

        void Finish()
        {
            if (!running) return;
            running = false;
            pending = null;
            try { capi.Event.UnregisterRenderer(this, stage); } catch { }

            if (taken.Count > 0)
                capi.ShowChatMessage($"[tallybook] {taken.Count} screenshot(s) written to {Folder}");
            if (skipped.Count > 0)
                capi.ShowChatMessage("[tallybook] skipped (nothing to photograph): "
                    + string.Join(", ", skipped));
            if (blank.Count > 0)
                capi.ShowChatMessage($"[tallybook] {blank.Count} shot(s) came back blank. The "
                    + "capture stage is the likely cause — run '.tallybook screenshots "
                    + (stage == EnumRenderStage.Done ? "stage final" : "stage done")
                    + "' to grab at the other one.");
            if (uncropped.Count > 0)
                capi.ShowChatMessage("[tallybook] left full-screen (window bounds unreadable): "
                    + string.Join(", ", uncropped));
            if (taken.Count == 0 && skipped.Count == 0 && blank.Count == 0)
                capi.ShowChatMessage("[tallybook] the showcase run produced nothing.");
        }

        public void Dispose()
        {
            if (running)
            {
                running = false;
                try { capi.Event.UnregisterRenderer(this, stage); } catch { }
            }
        }
    }
}
