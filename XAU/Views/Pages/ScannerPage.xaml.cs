using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using XAU.ViewModels.Pages;

namespace XAU.Views.Pages
{
    public partial class ScannerPage : INavigableView<ScannerViewModel>
    {
        public ScannerViewModel ViewModel { get; }

        public ScannerPage(ScannerViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
            InitializeComponent();

            ((INotifyCollectionChanged)ViewModel.LogLines).CollectionChanged += LogLines_CollectionChanged;
        }

        private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            }
        }

        private void OutputPathHyperlink_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ViewModel.OutputPath;
                if (string.IsNullOrWhiteSpace(path)) return;
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignored
            }
        }
    }
}
