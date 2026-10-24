using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Launcher.App.Controls;

public sealed class ServerCardPanel : Panel
{
    private const double MinimumCardWidth = 200;
    private const double MaximumCardWidth = 240;
    private int _columns = 1;
    private double _width = MaximumCardWidth;
    private const double Gap = 12;
    private double[] _rows = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var available = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MaximumCardWidth;
        _columns = Math.Max(1, (int)Math.Ceiling((available + Gap) / (MaximumCardWidth + Gap)));
        if ((available - (_columns - 1) * Gap) / _columns < MinimumCardWidth) _columns--;
        _columns = Math.Clamp(_columns, 1, Math.Max(1, Children.Count));
        _width = Math.Min(MaximumCardWidth, Math.Max(0, (available - (_columns - 1) * Gap) / _columns));
        _rows = new double[(Children.Count + _columns - 1) / _columns];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(_width, double.PositiveInfinity));
            _rows[i / _columns] = Math.Max(_rows[i / _columns], Children[i].DesiredSize.Height);
        }
        return new Size(available, _rows.Sum() + Math.Max(0, _rows.Length - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        for (var row = 0; row < _rows.Length; row++)
        {
            for (var col = 0; col < _columns; col++)
            {
                var i = row * _columns + col;
                if (i >= Children.Count) break;
                Children[i].Arrange(new Rect(col * (_width + Gap), top, _width, _rows[row]));
            }
            top += _rows[row] + Gap;
        }
        return finalSize;
    }
}
