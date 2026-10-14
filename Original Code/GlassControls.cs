using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

// A continuous-curvature corner: unlike a circular arc, curvature approaches
// zero at the join with each straight edge. WPF antialiases the vector path.
public class SmoothBorder : Border
{
    StreamGeometry outline;
    double lastWidth, lastHeight, lastRadius, lastThickness;
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double thickness = BorderThickness.Left;
        double inset = thickness / 2.0;
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(width, height) / 2.0);
        if (outline == null || width != lastWidth || height != lastHeight ||
            radius != lastRadius || thickness != lastThickness)
        {
            outline = MakeOutline(inset, inset, width - inset, height - inset, radius);
            lastWidth = width; lastHeight = height; lastRadius = radius; lastThickness = thickness;
        }
        Pen pen = BorderBrush != null && thickness > 0 ? new Pen(BorderBrush, thickness) : null;
        if (pen != null) pen.Freeze();
        dc.DrawGeometry(Background, pen, outline);
    }
    static StreamGeometry MakeOutline(double left, double top, double right, double bottom, double radius)
    {
        double r = Math.Max(0, Math.Min(radius, Math.Min(right-left, bottom-top)/2.0));
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext c = geometry.Open())
        {
            c.BeginFigure(new Point(left+r, top), true, true);
            c.LineTo(new Point(right-r, top), true, false);
            Corner(c, right-r, top+r, r, -Math.PI/2.0);
            c.LineTo(new Point(right, bottom-r), true, false);
            Corner(c, right-r, bottom-r, r, 0);
            c.LineTo(new Point(left+r, bottom), true, false);
            Corner(c, left+r, bottom-r, r, Math.PI/2.0);
            c.LineTo(new Point(left, top+r), true, false);
            Corner(c, left+r, top+r, r, Math.PI);
        }
        geometry.Freeze();
        return geometry;
    }
    static void Corner(StreamGeometryContext c, double cx, double cy, double r, double start)
    {
        const int samples = 128;
        for (int i=1; i<=samples; i++)
        {
            double angle = start + Math.PI * i / (2.0*samples);
            double x = Math.Cos(angle), y = Math.Sin(angle);
            double px = Math.Abs(x) < 1e-12 ? 0 : Math.Sign(x)*Math.Sqrt(Math.Abs(x));
            double py = Math.Abs(y) < 1e-12 ? 0 : Math.Sign(y)*Math.Sqrt(Math.Abs(y));
            c.LineTo(new Point(cx+r*px, cy+r*py), true, true);
        }
    }
}

