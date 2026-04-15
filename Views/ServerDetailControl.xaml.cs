using System;
using Windows.System;

namespace XrayUI.Views
{
    public sealed partial class ServerDetailControl
    {
        public ServerDetailViewModel ViewModel { get; set; } = null!;

        public ServerDetailControl()
        {
            this.InitializeComponent();
        }
        
        private void ShadowRect_Loaded(object sender, RoutedEventArgs e)
        {
            Shadow1.Receivers.Add(AIShadowCastGrid);
            Shadow2.Receivers.Add(AIShadowCastGrid);
            Shadow3.Receivers.Add(AIShadowCastGrid);
        }

        private async void AiLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
                return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;

            await Launcher.LaunchUriAsync(uri);
        }
    }
}
