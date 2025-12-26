namespace VfdWpfApp.ViewModels;

public sealed class AxisLabelViewModel
{
    public double Left { get; }
    public double Top { get; }
    public string Text { get; }

    public AxisLabelViewModel(double left, double top, string text)
    {
        Left = left;
        Top = top;
        Text = text;
    }
}
