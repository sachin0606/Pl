using System.Globalization;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class MusicPackageWindow : Window
{
    private readonly MainViewModel _vm;

    public MusicPackageWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _vm = vm;

        var s = vm.Settings.MusicPackage;
        EnabledCheck.IsChecked = s.Enabled;
        VideoBoxCheck.IsChecked = vm.Settings.ProfessionalBroadcast.NextItemVideo;
        CategoriesBox.Text = s.Categories;
        FirstNextBox.Text = Format(s.FirstNextAtSeconds);
        NextHoldBox.Text = Format(s.NextHoldSeconds);
        LaterHoldBox.Text = Format(s.LaterHoldSeconds);
        EndNextBox.Text = Format(s.EndNextBeforeSeconds);
        PreviewStartBox.Text = Format(s.PreviewStartPercent);
        var names = vm.CgProjects.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        InfoTemplateBox.ItemsSource = names;
        BoxTemplateBox.ItemsSource = names;
        InfoTemplateBox.Text = s.InfoTemplate;
        BoxTemplateBox.Text = s.BoxTemplate;
        InfoLayerBox.Text = s.InfoLayer.ToString(CultureInfo.InvariantCulture);
        BoxLayerBox.Text = s.BoxLayer.ToString(CultureInfo.InvariantCulture);
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(FirstNextBox, 0, 3600, out var firstNext) || !TryRead(NextHoldBox, 2, 120, out var nextHold) ||
            !TryRead(LaterHoldBox, 0, 120, out var laterHold) || !TryRead(EndNextBox, 0, 3600, out var endNext) ||
            !TryRead(PreviewStartBox, 0, 90, out var previewStart) || !TryRead(InfoLayerBox, 0, 999, out var infoLayer) ||
            !TryRead(BoxLayerBox, 0, 999, out var boxLayer))
            return;
        if ((int)infoLayer == (int)boxLayer)
        {
            StatusText.Text = "The two graphics need different CG layers.";
            return;
        }

        var s = _vm.Settings.MusicPackage;
        s.Enabled = EnabledCheck.IsChecked == true;
        s.Categories = string.IsNullOrWhiteSpace(CategoriesBox.Text) ? "Music" : CategoriesBox.Text.Trim();
        s.FirstNextAtSeconds = firstNext;
        s.NextHoldSeconds = nextHold;
        s.LaterHoldSeconds = laterHold;
        s.EndNextBeforeSeconds = endNext;
        s.PreviewStartPercent = previewStart;
        s.InfoTemplate = string.IsNullOrWhiteSpace(InfoTemplateBox.Text) ? MusicPackageSettings.DefaultInfoTemplate : InfoTemplateBox.Text.Trim();
        s.BoxTemplate = string.IsNullOrWhiteSpace(BoxTemplateBox.Text) ? MusicPackageSettings.DefaultBoxTemplate : BoxTemplateBox.Text.Trim();
        s.InfoLayer = (int)infoLayer;
        s.BoxLayer = (int)boxLayer;
        _vm.Settings.ProfessionalBroadcast.NextItemVideo = VideoBoxCheck.IsChecked == true;
        _vm.ApplyMusicPackageSettings();
        Close();
    }

    private bool TryRead(System.Windows.Controls.TextBox box, double min, double max, out double value)
    {
        if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        StatusText.Text = $"Enter a number from {min} to {max}.";
        box.Focus();
        box.SelectAll();
        return false;
    }
}
