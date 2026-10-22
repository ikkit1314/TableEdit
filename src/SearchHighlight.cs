using System.ComponentModel;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TableEdit.Model;

namespace TableEdit;

/// <summary>
/// Shared search state the grid cells bind to. Bumping Version makes every realised cell re-evaluate
/// its background, so changing the search re-highlights without rebuilding the grid.
/// </summary>
public sealed class SearchHighlight : INotifyPropertyChanged
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC8, 0x2E));

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x58, 0xA6, 0xFF));

    private RowSearch? _search;
    private int _version;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Version => _version;

    public Func<Row, int, bool>? IsSelected { get; set; }

    public void Refresh()
    {
        _version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }

    public RowSearch? Search
    {
        get => _search;
        set
        {
            _search = value;
            _version++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        }
    }

    /// <summary>Converter for one column: (cell text, version) → highlight brush or none.</summary>
    public IMultiValueConverter ForColumn(int column) =>
        new FuncMultiValueConverter<object?, IBrush?>(values =>
        {
            var entries = values.ToArray();
            if (entries.Length > 2 && entries[2] is Row row && IsSelected?.Invoke(row, column) == true)
                return SelectionBrush;
            var cell = entries.FirstOrDefault() as string;
            return _search?.IsCellMatch(cell, column) == true ? MatchBrush : null;
        });
}
