using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace StreamCapturePro.Converters
{
    public class BoolToStartStopConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isCapturing)
            {
                return isCapturing ? "停止获取" : "开始获取";
            }
            return "开始获取";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isCapturing)
            {
                return new SymbolIcon { Symbol = isCapturing ? SymbolRegular.Stop24 : SymbolRegular.Play24 };
            }
            return new SymbolIcon { Symbol = SymbolRegular.Play24 };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToAppearanceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isCapturing)
            {
                return isCapturing ? ControlAppearance.Danger : ControlAppearance.Primary;
            }
            return ControlAppearance.Primary;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}