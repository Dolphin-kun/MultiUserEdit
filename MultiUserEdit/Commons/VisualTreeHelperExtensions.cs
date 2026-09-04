using System.Windows;
using System.Windows.Media;

namespace MultiUserEdit.Commons
{
    public static class VisualTreeHelperExtensions
    {
        public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }
            return null;
        }

        public static DependencyObject? FindVisualChildByTypeName(DependencyObject parent, string typeName)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var type = child.GetType();
                if (type.Name == typeName || type.FullName == typeName) return child;

                var result = FindVisualChildByTypeName(child, typeName);
                if (result != null) return result;
            }
            return null;
        }

        public static T? FindElementByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent is FrameworkElement element && element.Name == name)
                return element as T;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var found = FindElementByName<T>(child, name);
                if (found != null) return found;
            }

            return null;
        }

        public static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;
            else
                return FindVisualParent<T>(parentObject);
        }
    }
}