using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HTunes.App;

internal enum SyncGlyphSource { Music, Podcast }

// A light bit of whimsy for the device strip: while a sync runs, an iPod glyph fades in at
// the centre with a music glyph to its left and a podcast glyph to its right, and particles
// drift from whichever side is being synced into the iPod until the sync finishes.
public partial class MainWindow
{
    private const double SyncGlyphSpread = 74;
    // Music flows in blue, podcasts in red, so the two syncs read differently at a glance.
    private static readonly Brush SyncMusicBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x74, 0xC4)));
    private static readonly Brush SyncPodcastBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x3C, 0x3C)));
    private static readonly Brush SyncGlyphBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x69, 0x8F)));

    private readonly DispatcherTimer syncParticleTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private TextBlock? syncMusicGlyph, syncPodGlyph, syncIPodGlyph;
    private bool syncAnimationRunning;
    private SyncGlyphSource syncAnimationSource;
    private readonly Random syncAnimationRandom = new();

    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    private void InitializeSyncAnimation()
    {
        if (SyncAnimationLayer is null) return;
        syncMusicGlyph = MakeSyncGlyph("♫", SyncMusicBrush);
        syncPodGlyph = MakeSyncGlyph("◉", SyncPodcastBrush);
        syncIPodGlyph = MakeSyncGlyph("▣", SyncGlyphBrush);
        foreach (var glyph in new[] { syncMusicGlyph, syncIPodGlyph, syncPodGlyph }) SyncAnimationLayer.Children.Add(glyph);
        syncParticleTimer.Tick += (_, _) => SpawnSyncParticles();
        LayoutSyncGlyphs();
    }

    private static TextBlock MakeSyncGlyph(string glyph, Brush brush) => new()
    {
        Text = glyph,
        FontSize = 17,
        Foreground = brush,
        Opacity = 0,
        IsHitTestVisible = false
    };

    // Centre the three-glyph assembly on the strip, but pull it left so the right-hand
    // glyph never disappears behind the transcode combo / sync buttons on the right.
    private double AssemblyCentreX()
    {
        var width = SyncAnimationLayer.ActualWidth;
        var rightControlsLeft = width;
        foreach (var control in new FrameworkElement?[] { TranscodeComboBox, SyncCurrentButton, SyncAllButton })
        {
            if (control is not { Visibility: Visibility.Visible } || control.ActualWidth <= 0) continue;
            try
            {
                var x = control.TransformToVisual(SyncAnimationLayer).Transform(default).X;
                if (x > 0) rightControlsLeft = Math.Min(rightControlsLeft, x);
            }
            catch { /* not in the visual tree yet */ }
        }
        // Sit a little left of dead-centre, and keep clear breathing room before the combo.
        var maxCentre = rightControlsLeft - SyncGlyphSpread - 54;
        return Math.Max(SyncGlyphSpread + 34, Math.Min(width / 2 - 46, maxCentre));
    }

    private (double X, double Y) SyncGlyphCentre(SyncGlyphSource source)
    {
        var cx = AssemblyCentreX();
        var cy = SyncAnimationLayer.ActualHeight / 2;
        return source == SyncGlyphSource.Music ? (cx - SyncGlyphSpread, cy) : (cx + SyncGlyphSpread, cy);
    }

    private void LayoutSyncGlyphs()
    {
        if (SyncAnimationLayer is null || syncIPodGlyph is null || syncMusicGlyph is null || syncPodGlyph is null) return;
        var cx = AssemblyCentreX();
        var cy = SyncAnimationLayer.ActualHeight / 2;
        Place(syncIPodGlyph, cx, cy);
        Place(syncMusicGlyph, cx - SyncGlyphSpread, cy);
        Place(syncPodGlyph, cx + SyncGlyphSpread, cy);

        static void Place(TextBlock glyph, double x, double y)
        {
            glyph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(glyph, x - glyph.DesiredSize.Width / 2);
            Canvas.SetTop(glyph, y - glyph.DesiredSize.Height / 2);
        }
    }

    private void SyncAnimationLayer_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutSyncGlyphs();

    private void StartSyncAnimation(SyncGlyphSource source)
    {
        if (SyncAnimationLayer is null || syncIPodGlyph is null) return;
        syncAnimationSource = source;
        syncAnimationRunning = true;
        LayoutSyncGlyphs();
        FadeSyncGlyph(syncIPodGlyph, 0.85);
        FadeSyncGlyph(syncMusicGlyph!, source == SyncGlyphSource.Music ? 0.7 : 0);
        FadeSyncGlyph(syncPodGlyph!, source == SyncGlyphSource.Podcast ? 0.7 : 0);
        if (!syncParticleTimer.IsEnabled) syncParticleTimer.Start();
    }

    private void StopSyncAnimation()
    {
        syncAnimationRunning = false;
        syncParticleTimer.Stop();
        if (syncIPodGlyph is null) return;
        foreach (var glyph in new[] { syncMusicGlyph!, syncIPodGlyph, syncPodGlyph! }) FadeSyncGlyph(glyph, 0);
    }

    private static void FadeSyncGlyph(TextBlock glyph, double to) =>
        glyph.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 320 : 480)) { EasingFunction = new QuadraticEase() });

    private void SpawnSyncParticles()
    {
        if (!syncAnimationRunning || SyncAnimationLayer is null || syncIPodGlyph is null) return;
        if (SyncAnimationLayer.ActualWidth < SyncGlyphSpread * 2 + 40) return;
        var (sx, sy) = SyncGlyphCentre(syncAnimationSource);
        var cx = AssemblyCentreX();
        var cy = SyncAnimationLayer.ActualHeight / 2;
        var brush = syncAnimationSource == SyncGlyphSource.Podcast ? SyncPodcastBrush : SyncMusicBrush;
        var count = syncAnimationRandom.Next(1, 3);
        for (var i = 0; i < count; i++)
        {
            var size = 3.0 + syncAnimationRandom.NextDouble() * 3;
            var dot = new Ellipse { Width = size, Height = size, Fill = brush, IsHitTestVisible = false };
            var jitterY = (syncAnimationRandom.NextDouble() - 0.5) * 14;
            var startX = sx + (syncAnimationRandom.NextDouble() - 0.5) * 10;
            var startY = sy + jitterY;
            Canvas.SetLeft(dot, startX - size / 2);
            Canvas.SetTop(dot, startY - size / 2);
            SyncAnimationLayer.Children.Add(dot);

            var duration = TimeSpan.FromMilliseconds(620 + syncAnimationRandom.Next(320));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            dot.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(startX - size / 2, cx - size / 2, duration) { EasingFunction = ease });
            dot.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(startY - size / 2, cy - size / 2, duration) { EasingFunction = ease });
            var fade = new DoubleAnimation(0.9, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => SyncAnimationLayer.Children.Remove(dot);
            dot.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
