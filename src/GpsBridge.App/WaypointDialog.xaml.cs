using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GpsBridge.Core.Geo;
using GpsBridge.Core.Waypoints;

namespace GpsBridge.App;

public partial class WaypointDialog : Window
{
    readonly Waypoint? _orig;
    string _color;
    public Waypoint? Result { get; private set; }
    public bool Deleted { get; private set; }
    public bool GoTo { get; private set; }

    /// <param name="w">Punto da modificare, o null per crearne uno nuovo in (lat, lon).</param>
    public WaypointDialog(Waypoint? w, double lat, double lon, double? ele, CoordFormat fmt)
    {
        InitializeComponent();
        _orig = w;
        if (w is not null) { BtnDel.Visibility = Visibility.Visible; BtnGo.Visibility = Visibility.Visible; }
        Title = w is null ? "Nuovo punto" : "Modifica punto";
        _color = w?.Color ?? WaypointColors.Palette[0].Hex;
        TxtName.Text = w?.Name ?? "";
        TxtCoord.Text = double.IsNaN(w?.Lat ?? lat) ? "" : GeoMath.FormatCoord(w?.Lat ?? lat, w?.Lon ?? lon, fmt == CoordFormat.Utm ? CoordFormat.Decimal : fmt);
        TxtEle.Text = (w?.Ele ?? ele)?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
        TxtNotes.Text = w?.Notes ?? "";
        foreach (var (name, hex) in WaypointColors.Palette)
        {
            var rb = new RadioButton { GroupName = "c", Tag = hex, ToolTip = name, IsChecked = hex == _color, Margin = new Thickness(0, 0, 6, 0), Cursor = System.Windows.Input.Cursors.Hand };
            var c = (Color)ColorConverter.ConvertFromString(hex);
            rb.Template = SwatchTemplate();
            rb.Background = new SolidColorBrush(c);
            rb.Checked += (_, _) => _color = hex;
            Colors.Children.Add(rb);
        }
        Loaded += (_, _) => { TxtName.Focus(); TxtName.SelectAll(); };
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var fill = new FrameworkElementFactory(typeof(Ellipse), "F");
        fill.SetValue(WidthProperty, 24.0); fill.SetValue(HeightProperty, 24.0);
        fill.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        fill.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x41)));
        fill.SetValue(Shape.StrokeThicknessProperty, 2.0);
        grid.AppendChild(fill);
        t.VisualTree = grid;
        var trig = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        trig.Setters.Add(new Setter(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), "F"));
        trig.Setters.Add(new Setter(Shape.StrokeThicknessProperty, 3.0, "F"));
        t.Triggers.Add(trig);
        return t;
    }

    void Del_Click(object s, RoutedEventArgs e) { Deleted = true; DialogResult = true; }
    void Go_Click(object s, RoutedEventArgs e) { GoTo = true; DialogResult = true; }

    void Ok_Click(object s, RoutedEventArgs e)
    {
        if (!GeoMath.TryParseCoord(TxtCoord.Text, out var lat, out var lon))
        {
            TxtErr.Text = "Coordinate non riconosciute. Esempi: 45.5565, 10.2350  oppure  45°33'23\"N 10°14'06\"E";
            TxtErr.Visibility = Visibility.Visible; return;
        }
        double? ele = double.TryParse(TxtEle.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var el) ? el : null;
        var name = string.IsNullOrWhiteSpace(TxtName.Text) ? "Punto" : TxtName.Text.Trim();
        Result = _orig is null
            ? Waypoint.Create(name, lat, lon, ele, _color, TxtNotes.Text.Trim())
            : _orig with { Name = name, Lat = lat, Lon = lon, Ele = ele, Color = _color, Notes = TxtNotes.Text.Trim() };
        DialogResult = true;
    }
}
