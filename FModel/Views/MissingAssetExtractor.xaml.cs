using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

public partial class MissingAssetExtractor
{
    private MissingAssetExtractorViewModel ViewModel => (MissingAssetExtractorViewModel) DataContext;

    public MissingAssetExtractor()
    {
        DataContext = new MissingAssetExtractorViewModel();
        InitializeComponent();
    }

    private async void OnExtractClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ExtractAsync();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Reset();
    }
}
