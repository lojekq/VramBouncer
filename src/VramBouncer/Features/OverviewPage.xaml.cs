using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public partial class OverviewPage
{
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public OverviewPage()
    {
        InitializeComponent();
        DataContext = Vm.I;
        EvictButton.Click += (_, _) => Actions.EvictNow();
        RestoreButton.Click += (_, _) => Actions.RestoreNow();
        PauseButton.Flyout = Actions.PauseMenu();
        tick.Tick += (_, _) => HeaderText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget(); // the game timer and the grace countdown
        Loaded += (_, _) => { Vm.I.Changed += Draw; tick.Start(); Draw(); };
        Unloaded += (_, _) => { Vm.I.Changed -= Draw; tick.Stop(); };
    }

    void UseMLink(object sender, RoutedEventArgs e) => Engine.UseMLink();

    /// <summary>The VRAM bar: one star column per segment, legend chips below.</summary>
    void Draw()
    {
        BarGrid.Children.Clear();
        BarGrid.ColumnDefinitions.Clear();
        Legend.Children.Clear();
        var total = Math.Max(1, Vm.I.Bar.Sum(s => s.Bytes));
        foreach (var s in Vm.I.Bar)
        {
            if (s.Bytes <= 0) continue;
            BarGrid.ColumnDefinitions.Add(new() { Width = new GridLength(s.Bytes / (double)total, GridUnitType.Star) });
            var cell = new Border { Background = s.Fill, Margin = new(0, 0, 1, 0), ToolTip = $"{s.Label}: {Engine.Gb(s.Bytes)} {Loc.T("Unit.GB")}" };
            Grid.SetColumn(cell, BarGrid.ColumnDefinitions.Count - 1);
            BarGrid.Children.Add(cell);

            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 18, 4) };
            chip.Children.Add(new Rectangle
            {
                Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = s.Fill, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Stroke = (System.Windows.Media.Brush)FindResource("ControlStrokeColorDefaultBrush"), StrokeThickness = 1,
            });
            chip.Children.Add(new Wpf.Ui.Controls.TextBlock { Text = $"{s.Label} {Engine.Gb(s.Bytes)} {Loc.T("Unit.GB")}", FontTypography = Wpf.Ui.Controls.FontTypography.Caption });
            Legend.Children.Add(chip);
        }
    }
}
