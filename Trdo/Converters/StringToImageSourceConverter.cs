using Microsoft.UI.Xaml.Data;
using System;
using Trdo.Helpers;

namespace Trdo.Converters;

/// <summary>
/// Converts a string URL to an <see cref="Microsoft.UI.Xaml.Media.ImageSource"/>, returning null
/// for invalid or empty URLs. Goes through <see cref="ImageSourceFactory"/> so a local album's
/// cover.jpg loads in the station list the same way it does in the now-playing surfaces.
/// </summary>
public partial class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        value is string url ? ImageSourceFactory.Create(url) : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
