namespace VfdWpfApp.ViewModels;

public sealed class AxisLabelViewModel
{
    public double Left { get; }
    public string Text { get; }

    public AxisLabelViewModel(double left, string text)
    {
        Left = left;
        Text = text;
    }
}
