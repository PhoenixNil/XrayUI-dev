using System;

namespace XrayUI.Views
{
    public sealed partial class PersonalizeControl
    {
        public PersonalizeViewModel ViewModel { get; set; } = null!;

        public PersonalizeControl()
        {
            this.InitializeComponent();
        }

        private async void ExportPresetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportDir = await ViewModel.ExportPresetAsync();
                ExportSuccessInfoBar.Severity = InfoBarSeverity.Success;
                ExportSuccessInfoBar.Title = "导出成功";
                ExportSuccessInfoBar.Message = $"已导出至 {exportDir}";
                ExportSuccessInfoBar.IsOpen = true;
            }
            catch (Exception ex)
            {
                ExportSuccessInfoBar.Severity = InfoBarSeverity.Error;
                ExportSuccessInfoBar.Title = "导出失败";
                ExportSuccessInfoBar.Message = ex.Message;
                ExportSuccessInfoBar.IsOpen = true;
            }
        }
    }
}
