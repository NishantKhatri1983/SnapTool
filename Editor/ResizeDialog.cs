using System;
using System.Windows;
using System.Windows.Controls;

namespace SnapTool.Editor;

/// <summary>Asks for a new image size in pixels (or a percentage), keeping the aspect ratio by default.</summary>
public sealed class ResizeDialog : Window
{
    readonly int _origW, _origH;
    readonly TextBox _w, _h;
    readonly CheckBox _keep;
    bool _syncing;

    public int ResultWidth { get; private set; }
    public int ResultHeight { get; private set; }

    public ResizeDialog(int width, int height)
    {
        _origW = width;
        _origH = height;
        Title = "Resize image";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        _w = new TextBox { Text = width.ToString(), Width = 80, Margin = new Thickness(6, 3, 6, 3) };
        _h = new TextBox { Text = height.ToString(), Width = 80, Margin = new Thickness(6, 3, 6, 3) };
        _keep = new CheckBox { Content = "Keep aspect ratio", IsChecked = true, Margin = new Thickness(0, 8, 0, 4) };
        _w.TextChanged += (_, _) => Sync(fromWidth: true);
        _h.TextChanged += (_, _) => Sync(fromWidth: false);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Add(grid, new TextBlock { Text = $"Current size: {width} × {height} px", Margin = new Thickness(0, 0, 0, 8) }, 0, 0, 3);
        Add(grid, new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Add(grid, _w, 1, 1);
        Add(grid, new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center }, 1, 2);
        Add(grid, new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center }, 2, 0);
        Add(grid, _h, 2, 1);
        Add(grid, new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center }, 2, 2);
        Add(grid, _keep, 3, 0, 3);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        foreach (var pct in new[] { 25, 50, 75, 150, 200 })
        {
            var b = new Button { Content = $"{pct}%", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) =>
            {
                _syncing = true;
                _w.Text = Math.Max(1, (int)Math.Round(_origW * pct / 100.0)).ToString();
                _h.Text = Math.Max(1, (int)Math.Round(_origH * pct / 100.0)).ToString();
                _syncing = false;
            };
            presets.Children.Add(b);
        }
        Add(grid, presets, 4, 0, 3);

        var ok = new Button { Content = "Resize", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Add(grid, buttons, 5, 0, 3);

        Content = grid;
        Loaded += (_, _) => { _w.Focus(); _w.SelectAll(); };
    }

    static void Add(Grid g, UIElement e, int row, int col, int span = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetColumnSpan(e, span);
        g.Children.Add(e);
    }

    void Sync(bool fromWidth)
    {
        if (_syncing || _keep.IsChecked != true) return;
        _syncing = true;
        if (fromWidth && int.TryParse(_w.Text, out int w) && w > 0)
            _h.Text = Math.Max(1, (int)Math.Round(w * (double)_origH / _origW)).ToString();
        else if (!fromWidth && int.TryParse(_h.Text, out int h) && h > 0)
            _w.Text = Math.Max(1, (int)Math.Round(h * (double)_origW / _origH)).ToString();
        _syncing = false;
    }

    void Accept()
    {
        if (!int.TryParse(_w.Text, out int w) || !int.TryParse(_h.Text, out int h) || w < 1 || h < 1 || w > 20000 || h > 20000)
        {
            MessageBox.Show(this, "Enter a width and height between 1 and 20000 pixels.", "Resize image");
            return;
        }
        ResultWidth = w;
        ResultHeight = h;
        DialogResult = true;
    }
}
