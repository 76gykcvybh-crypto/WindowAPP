using System.Windows.Media;

namespace VfdWpfApp.ViewModels;

public sealed class SeriesScaleLabelViewModel
{
    public string Label { get; }
    public Brush Stroke { get; }
    public double Scale { get; }
    public double Offset { get; }
    public double Min { get; }
    public double Max { get; }

    public SeriesScaleLabelViewModel(
        string label,
        Brush stroke,
        double scale,
        double offset,
        double min,
        double max)
    {
        Label = label;
        Stroke = stroke;
        Scale = scale;
        Offset = offset;
        Min = min;
        Max = max;
    }
}
