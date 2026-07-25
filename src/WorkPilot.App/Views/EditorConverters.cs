using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WorkPilot.Views;

/// <summary>Formats a <see cref="DateTimeOffset"/> for the trigger preview list.</summary>
public sealed class DateTimeOffsetToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DateTimeOffset dto)
            return dto.ToLocalTime().ToString("g");
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Shows a marker when a monthly missing-day occurrence was skipped.</summary>
public sealed class BoolToSkippedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "（跳过短月）" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Bridges a nullable <see cref="long"/> (e.g. trigger interval seconds) and a NumberBox's double Value.</summary>
public sealed class NullableLongToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is long l ? (double)l : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
            return (long)Math.Round(d);
        return 0L;
    }
}

/// <summary>Shows a marker for a disabled (excluded) workflow node.</summary>
public sealed class BoolToDisabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "（已禁用）" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="bool"/> to <see cref="Visibility"/> for the Security Center tab panels.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Bridges the Security Center audit limit (<see cref="int"/>) and a NumberBox's double Value.</summary>
public sealed class DoubleToIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int i ? (double)i : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double d ? (int)Math.Round(d) : 0;
}
