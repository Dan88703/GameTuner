using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace GameTuner.App.Controls;

/// <summary>A chart point: X from 0 (left edge) to 1 (right edge); Y null = no data (gap in the line).</summary>
public readonly record struct ChartPoint(double X, double? Y);

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ChartPoint> Points);

/// <summary>Everything the chart needs to draw itself. Immutable: a new instance = a redraw.</summary>
public sealed record ChartData(
    IReadOnlyList<ChartSeries> Series,
    double YMin,
    double YMax,
    string Unit,
    string LeftLabel,
    string RightLabel,
    string EmptyText = "Нет данных")
{
    public bool HasData => Series.Any(s => s.Points.Any(p => p.Y.HasValue));
}

/// <summary>
/// Minimal line chart for WinUI 3, drawn with plain shapes on a Canvas.
///
/// Why not a chart library: the popular WinUI chart packages are large or still
/// in preview, and we only need simple time-series lines. Drawing ourselves is
/// ~150 lines and has no dependency that can break on the next update.
///
/// How it draws:
///   1. 5 horizontal grid lines with value labels on the left;
///   2. time labels under the plot ("−5 мин" … "сейчас");
///   3. each series as one or more Polylines — the line is split wherever a
///      value is missing, so a gap in data is shown as a gap, not a fake drop to 0;
///   4. a legend above the plot with each series' latest value.
/// It redraws when <see cref="Data"/> changes or the control is resized.
/// </summary>
public sealed class LineChart : UserControl
{
    private const double LeftMargin = 44, RightMargin = 8, TopMargin = 8, BottomMargin = 22;
    private const int GridLines = 5;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(LineChart),
        new PropertyMetadata(null, (d, _) => ((LineChart)d).Redraw()));

    private readonly Canvas _canvas = new();
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(0, 0, 0, 8) };

    public LineChart()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_canvas, 1);
        root.Children.Add(_legend);
        root.Children.Add(_canvas);
        Content = root;

        MinHeight = 160;
        _canvas.SizeChanged += (_, _) => Redraw();
    }

    public ChartData? Data
    {
        get => (ChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        _legend.Children.Clear();

        var data = Data;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        if (data == null || width <= LeftMargin + RightMargin || height <= TopMargin + BottomMargin) return;

        var gridBrush = ThemeBrush("DividerStrokeColorDefaultBrush", ColorHelper.FromArgb(40, 128, 128, 128));
        var labelBrush = ThemeBrush("TextFillColorSecondaryBrush", ColorHelper.FromArgb(200, 160, 160, 160));

        double plotW = width - LeftMargin - RightMargin;
        double plotH = height - TopMargin - BottomMargin;
        double range = Math.Max(data.YMax - data.YMin, 1e-6);

        // Value → pixel. Screen Y grows downwards, so YMax maps to the top margin.
        double ToX(double x) => LeftMargin + Math.Clamp(x, 0, 1) * plotW;
        double ToY(double y) => TopMargin + (1 - (Math.Clamp(y, data.YMin, data.YMax) - data.YMin) / range) * plotH;

        // 1. Horizontal grid + Y labels.
        for (int i = 0; i < GridLines; i++)
        {
            double value = data.YMin + range * i / (GridLines - 1);
            double y = ToY(value);
            _canvas.Children.Add(new Line { X1 = LeftMargin, X2 = LeftMargin + plotW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            AddLabel($"{value:0}{data.Unit}", 0, y - 8, labelBrush, LeftMargin - 6, TextAlignment.Right);
        }

        // 2. Time labels.
        AddLabel(data.LeftLabel, LeftMargin, TopMargin + plotH + 4, labelBrush, 120, TextAlignment.Left);
        AddLabel(data.RightLabel, LeftMargin + plotW - 120, TopMargin + plotH + 4, labelBrush, 120, TextAlignment.Right);

        if (!data.HasData)
        {
            AddLabel(data.EmptyText, LeftMargin, TopMargin + plotH / 2 - 10, labelBrush, plotW, TextAlignment.Center);
            return;
        }

        foreach (var series in data.Series)
        {
            var brush = new SolidColorBrush(series.Color);

            // 3. Lines, split into segments at missing values.
            var segment = new PointCollection();
            foreach (var p in series.Points)
            {
                if (p.Y is double v)
                {
                    segment.Add(new Point(ToX(p.X), ToY(v)));
                }
                else
                {
                    AddSegment(segment, brush);
                    segment = new PointCollection();
                }
            }
            AddSegment(segment, brush);

            // 4. Legend entry with the latest value.
            double? last = series.Points.LastOrDefault(p => p.Y.HasValue).Y;
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            entry.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock
            {
                Text = last is double lv ? $"{series.Name}: {lv:0}{data.Unit}" : $"{series.Name}: —",
                FontSize = 13
            });
            _legend.Children.Add(entry);
        }
    }

    private void AddSegment(PointCollection points, Brush brush)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            // A single point has no line: draw a small dot so it's still visible.
            var dot = new Ellipse { Width = 4, Height = 4, Fill = brush };
            Canvas.SetLeft(dot, points[0].X - 2);
            Canvas.SetTop(dot, points[0].Y - 2);
            _canvas.Children.Add(dot);
            return;
        }
        _canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void AddLabel(string text, double left, double top, Brush brush, double width, TextAlignment align)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Foreground = brush, Width = width, TextAlignment = align };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        _canvas.Children.Add(label);
    }

    /// <summary>Theme brush from the app resources (follows light/dark theme), with a fallback.</summary>
    private static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    /// <summary>Series colours of the GameTuner theme (on a black background).</summary>
    public static class Palette
    {
        public static readonly Color Cpu = ColorHelper.FromArgb(255, 255, 59, 78);    // red
        public static readonly Color Gpu = ColorHelper.FromArgb(255, 58, 134, 255);   // blue
        public static readonly Color Ram = ColorHelper.FromArgb(255, 236, 236, 242);  // white
        public static readonly Color Fps = ColorHelper.FromArgb(255, 255, 183, 3);    // amber
    }
}
