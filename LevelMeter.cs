using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WindowAudioRecorder;

/// <summary>A horizontal peak meter with a decaying peak-hold marker and a clip indicator.</summary>
public sealed class LevelMeter : Control
{
    private const float FloorDb = -60f;
    private const float PeakDecayPerUpdate = 0.015f;
    private const int ClipHoldUpdates = 60;   // ~2 s at the form's refresh rate

    private static readonly Color ClipColor = Color.FromArgb(214, 62, 62);

    private float _level;   // 0..1, dB-scaled
    private float _hold;    // 0..1, dB-scaled
    private float _peakDb = FloorDb;
    private int _clipHold;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        TabStop = false;
    }

    /// <summary>Channel label drawn at the left edge, e.g. "L".</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = string.Empty;

    /// <summary>Feeds the meter a linear peak amplitude in the range 0..1.</summary>
    public void SetPeak(float amplitude, bool clipped = false)
    {
        _peakDb = amplitude <= 0.0000001f ? FloorDb : Math.Max(FloorDb, 20f * MathF.Log10(amplitude));
        _level = (_peakDb - FloorDb) / -FloorDb;
        _hold = _level >= _hold ? _level : Math.Max(_level, _hold - PeakDecayPerUpdate);

        if (clipped) _clipHold = ClipHoldUpdates;
        else if (_clipHold > 0) _clipHold--;

        Invalidate();
    }

    public void Reset()
    {
        _level = 0f;
        _hold = 0f;
        _peakDb = FloorDb;
        _clipHold = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Measured, not hard-coded: the font grows with the display scaling.
        int labelWidth = string.IsNullOrEmpty(Caption) ? 0 : (int)Math.Ceiling(g.MeasureString("W", Font).Width) + 4;
        int readoutWidth = (int)Math.Ceiling(g.MeasureString("-99.9 dB", Font).Width) + 6;
        int clipWidth = (int)Math.Ceiling(g.MeasureString("CLIP", Font).Width) + 8;
        var track = new Rectangle(labelWidth, 0, Math.Max(1, Width - labelWidth - readoutWidth - clipWidth), Height);

        using (var back = new SolidBrush(Color.FromArgb(232, 234, 238)))
        {
            g.FillRectangle(back, track);
        }

        int filled = (int)(track.Width * Math.Clamp(_level, 0f, 1f));
        if (filled > 0)
        {
            // One gradient across the whole track, clipped to the filled part, so a given level
            // always paints the same colour regardless of how far the bar currently reaches.
            using var gradient = new LinearGradientBrush(track, Color.FromArgb(46, 184, 114), ClipColor, LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(46, 184, 114), Color.FromArgb(46, 184, 114), Color.FromArgb(228, 184, 40), ClipColor],
                    Positions = [0f, 0.62f, 0.85f, 1f]
                }
            };
            g.FillRectangle(gradient, new Rectangle(track.X, track.Y, filled, track.Height));
        }

        if (_hold > 0.001f)
        {
            int x = track.X + Math.Clamp((int)(track.Width * _hold), 1, track.Width) - 2;
            using var pen = new Pen(Color.FromArgb(64, 68, 78), 2f);
            g.DrawLine(pen, x, track.Top + 1, x, track.Bottom - 1);
        }

        using var text = new SolidBrush(Color.FromArgb(96, 100, 110));
        var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (labelWidth > 0)
        {
            g.DrawString(Caption, Font, text, new RectangleF(0, 0, labelWidth, Height),
                new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
        }

        string readout = _peakDb <= FloorDb ? "-∞ dB" : $"{_peakDb,5:0.0} dB";
        g.DrawString(readout, Font, text, new RectangleF(track.Right, 0, readoutWidth, Height),
            new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });

        var clipBox = new RectangleF(track.Right + readoutWidth + 4, 1, clipWidth - 6, Height - 2);
        if (_clipHold > 0)
        {
            using var clipFill = new SolidBrush(ClipColor);
            using var clipText = new SolidBrush(Color.White);
            g.FillRectangle(clipFill, clipBox);
            g.DrawString("CLIP", Font, clipText, clipBox, centered);
        }
        else
        {
            using var idle = new SolidBrush(Color.FromArgb(216, 219, 224));
            g.DrawString("CLIP", Font, idle, clipBox, centered);
        }
    }
}
