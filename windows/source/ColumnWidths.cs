using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace FluentScanner;

/// Shared column widths for the results table (header + every row bind to
/// the same instance via `{Binding W0, Source={StaticResource Cols}}`, so a
/// gripper drag resizes header and rows together — WinUI has no DataGrid).
internal sealed class ColumnWidths : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private GridLength _w0 = new(170);
    private GridLength _w1 = new(110);
    private GridLength _w2 = new(150);
    private GridLength _w3 = new(130);
    private GridLength _w4 = new(110);
    private GridLength _w5 = new(220);
    private GridLength _w6 = new(70);

    public GridLength W0 { get => _w0; set => Set(ref _w0, value); }
    public GridLength W1 { get => _w1; set => Set(ref _w1, value); }
    public GridLength W2 { get => _w2; set => Set(ref _w2, value); }
    public GridLength W3 { get => _w3; set => Set(ref _w3, value); }
    public GridLength W4 { get => _w4; set => Set(ref _w4, value); }
    public GridLength W5 { get => _w5; set => Set(ref _w5, value); }
    public GridLength W6 { get => _w6; set => Set(ref _w6, value); }

    internal GridLength this[int i] => i switch
    {
        0 => W0, 1 => W1, 2 => W2, 3 => W3, 4 => W4, 5 => W5, _ => W6,
    };

    internal void Set(int i, double pixels)
    {
        var w = new GridLength(Math.Max(50, pixels));
        switch (i)
        {
            case 0: W0 = w; break;
            case 1: W1 = w; break;
            case 2: W2 = w; break;
            case 3: W3 = w; break;
            case 4: W4 = w; break;
            case 5: W5 = w; break;
            case 6: W6 = w; break;
        }
    }

    private void Set(ref GridLength field, GridLength value, [CallerMemberName] string? name = null)
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
