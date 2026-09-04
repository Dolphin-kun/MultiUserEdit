using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiUserEdit.Views.Controls
{
    public partial class FileTransferBanner : UserControl
    {
        private bool isExpanded = true;
        private MouseButtonEventHandler? externalClickHandler;

        public FileTransferBanner()
        {
            InitializeComponent();
            SetExpanded(isExpanded);
            // バナー外クリックで自動的に折りたたむ機能があり、ドラッグ＆ドロップ等の転送開始操作そのものが
            // 「外側でのクリック」として扱われ、表示された直後に折りたたまれてしまうことがあった。
            // 転送が新しく始まる（非表示→表示）たびに展開状態へリセットする。
            RootGrid.IsVisibleChanged += (s, e) =>
            {
                if (RootGrid.IsVisible) SetExpanded(true);
            };
            Loaded += FileTransferBanner_Loaded;
            Unloaded += FileTransferBanner_Unloaded;
        }

        private void FileTransferBanner_Loaded(object sender, RoutedEventArgs e)
        {
            externalClickHandler = (s, args) =>
            {
                if (!isExpanded) return;

                var pos = args.GetPosition(this);
                if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
                {
                    SetExpanded(false);
                }
            };

            if (Application.Current?.MainWindow != null)
            {
                Application.Current.MainWindow.PreviewMouseDown += externalClickHandler;
            }
        }

        private void FileTransferBanner_Unloaded(object sender, RoutedEventArgs e)
        {
            if (externalClickHandler != null && Application.Current?.MainWindow != null)
            {
                Application.Current.MainWindow.PreviewMouseDown -= externalClickHandler;
            }
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            SetExpanded(!isExpanded);
        }

        private void SetExpanded(bool expanded)
        {
            isExpanded = expanded;
            DetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            // 展開中は「上向き（閉じる）」、折りたたみ中は「下向き（開く）」の向きにする
            // （下向き矢印＝これ以上開ける、というのが一般的な向きの慣習のため）
            ArrowPath.Data = Geometry.Parse(expanded
                ? "M7.41,15.41L12,10.83L16.59,15.41L18,14L12,8L6,14L7.41,15.41Z"
                : "M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z");
        }
    }
}
