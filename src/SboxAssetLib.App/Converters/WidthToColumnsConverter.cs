using System.Globalization;
using Avalonia.Data.Converters;

namespace SboxAssetLib.App.Converters;

/// <summary>
/// Maps an available width to the number of equal columns that fit, so a <c>UniformGrid</c> can lay
/// the gallery out responsively: cards stretch to fill the row (no leftover side gap) and snap to a
/// new column once there's room — the same behaviour as CSS <c>repeat(auto-fill, minmax(N, 1fr))</c>.
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    /// <summary>Minimum width a single card cell should occupy, including its margins.</summary>
    public double CellWidth { get; set; } = 186;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double w && w > 0 ? Math.Max(1, (int)(w / CellWidth)) : 1;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
