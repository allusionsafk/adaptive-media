using System.Windows;

namespace AdaptiveMedia;

public partial class SettingsWindow : Window
{
    public AppSettings ResultSettings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowTheme.UseDarkFrame(this);
        ResultSettings = settings;
        HdrCheck.IsChecked = false;
        HdrCheck.IsEnabled = false;
        ExternalCheck.IsChecked = settings.PreferExternalDisplay;
        FullscreenCheck.IsChecked = settings.FullscreenExternal;
        BitstreamCheck.IsChecked = settings.HdmiBitstream;
        MpcCheck.IsChecked = false;
        MpcCheck.IsEnabled = false;
        NativeDvCheck.IsChecked = settings.NativeDolbyVisionLane;
        NativeDvDownloadCheck.IsChecked = settings.AllowNativeDolbyVisionDownload;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultSettings.AutoHdrSwitch = false;
        ResultSettings.PreferExternalDisplay = ExternalCheck.IsChecked == true;
        ResultSettings.FullscreenExternal = FullscreenCheck.IsChecked == true;
        ResultSettings.HdmiBitstream = BitstreamCheck.IsChecked == true;
        ResultSettings.MpcFallback = false;
        ResultSettings.NativeDolbyVisionLane = NativeDvCheck.IsChecked == true;
        ResultSettings.AllowNativeDolbyVisionDownload = NativeDvDownloadCheck.IsChecked == true;
        DialogResult = true;
    }
}
