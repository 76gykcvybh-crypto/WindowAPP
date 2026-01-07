using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace VfdWpfApp.ViewModels;

public sealed class ChartSeriesViewModel : NotifyBase
{
    public string Label { get; }
    public Brush Stroke { get; }

    private PointCollection _points = new();
    public PointCollection Points { get => _points; private set => Set(ref _points, value); }

    private double _scale = 1.0;
    public double Scale { get => _scale; set => Set(ref _scale, Math.Max(0.0001, value)); }

    private double _offset;
    public double Offset { get => _offset; set => Set(ref _offset, value); }

    public ChartSeriesViewModel(string label, Brush stroke)
    {
        Label = label;
        Stroke = stroke;
    }

    public void UpdatePoints(IReadOnlyList<double> samples, double min, double max, double width, double height)
    {
        var points = new PointCollection();
        if (samples.Count == 0)
        {
            Points = points;
            return;
        }

        double range = Math.Max(1, max - min);
        double usableWidth = Math.Max(1, width - 1);
        double usableHeight = Math.Max(1, height - 1);
        double xStep = samples.Count <= 1 ? 0 : usableWidth / (samples.Count - 1);

        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * xStep;
            double scaled = samples[i] * Scale + Offset;
            double normalized = (scaled - min) / range;
            double y = usableHeight - (normalized * usableHeight);
            points.Add(new Point(x, y));
        }

        Points = points;
    }
}
