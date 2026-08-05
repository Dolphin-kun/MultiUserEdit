using MultiUserEdit.Commons.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MultiUserEdit.Views.Converters
{
    public class RoleToPencilVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is bool isHost && values[1] is UserRole role)
            {
                if (isHost && role == UserRole.Guest)
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
