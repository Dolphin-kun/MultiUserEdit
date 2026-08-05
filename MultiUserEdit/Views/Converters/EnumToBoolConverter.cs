using System.Globalization;
using System.Windows.Data;

namespace MultiUserEdit.Views.Converters
{
    public class EnumToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            return value.Equals(parameter);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolVal && boolVal && parameter != null)
                return parameter;

            return Binding.DoNothing;
        }
    }
}
