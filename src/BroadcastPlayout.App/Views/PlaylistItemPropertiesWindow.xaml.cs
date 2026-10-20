using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Views;

public partial class PlaylistItemPropertiesWindow : Window
{
    public PlaylistItem Item { get; }
    public PlaylistItemPropertiesWindow(PlaylistItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        InitializeComponent();
        DataContext = this;
        ConfigureControlPanels();
    }

    private void ConfigureControlPanels()
    {
        DtmfPanel.Visibility = Item.IsDtmfEvent ? Visibility.Visible : Visibility.Collapsed;
        DialTonePanel.Visibility = Item.IsDialToneEvent ? Visibility.Visible : Visibility.Collapsed;
        TcpPanel.Visibility = Item.IsTcpEvent ? Visibility.Visible : Visibility.Collapsed;
        MosPanel.Visibility = Item.IsMosEvent ? Visibility.Visible : Visibility.Collapsed;
        Scte35Panel.Visibility = Item.EventType == "SCTE35" ? Visibility.Visible : Visibility.Collapsed;
        Scte104Panel.Visibility = Item.EventType == "SCTE104" ? Visibility.Visible : Visibility.Collapsed;
        Caption608Panel.Visibility = Item.EventType == "CAPTION608" ? Visibility.Visible : Visibility.Collapsed;
        Caption708Panel.Visibility = Item.EventType == "CAPTION708" ? Visibility.Visible : Visibility.Collapsed;
        DvbSubPanel.Visibility = Item.EventType == "DVBSUB" ? Visibility.Visible : Visibility.Collapsed;
        GpoPanel.Visibility = Item.EventType == "GPO" ? Visibility.Visible : Visibility.Collapsed;
        Rs422Panel.Visibility = Item.EventType == "RS422" ? Visibility.Visible : Visibility.Collapsed;
        VdcpPanel.Visibility = Item.EventType == "VDCP" ? Visibility.Visible : Visibility.Collapsed;
        SnmpPanel.Visibility = Item.EventType == "SNMP" ? Visibility.Visible : Visibility.Collapsed;
        ControlSignalTab.Visibility = Item.IsDtmfEvent || Item.IsDialToneEvent || Item.IsTcpEvent || Item.IsMosEvent || Item.IsProfessionalControlEvent || Item.EventType == "SCTE35" ? Visibility.Visible : Visibility.Collapsed;
        CategoryCombo.IsEnabled = !Item.IsControlEvent;
        EventTypeCombo.IsEnabled = !Item.IsControlEvent;
        ManagedCategoryHint.Visibility = Item.IsControlEvent ? Visibility.Visible : Visibility.Collapsed;
        if (ControlSignalTab.Visibility == Visibility.Visible) ControlSignalTab.IsSelected = true;
    }
    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Apply_Click(object s, RoutedEventArgs e)
    {
        var validation = ValidateEventProperties();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            MessageBox.Show(this, validation, "Event properties", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Item.RefreshLegacyControlNotes();
        BroadcastPlayout.Services.AuditLogService.Write("ITEM_PROPERTIES", Item.Title, Item.IsControlEvent ? Item.Notes : Item.MediaPropertiesSummary);
        DialogResult = true;
    }

    private string? ValidateEventProperties()
    {
        var type = (Item.EventType ?? string.Empty).Trim().ToUpperInvariant();
        if (type is "CAPTION608" or "CAPTION708" or "DVBSUB")
        {
            if (string.IsNullOrWhiteSpace(Item.CaptionText)) return "Enter caption/subtitle text before applying the event.";
        }
        if (type == "DVBSUB")
        {
            if ((Item.DvbLanguage ?? string.Empty).Trim().Length != 3) return "DVB subtitle language must be a 3-letter ISO 639 code, for example eng or nep.";
        }
        if (type == "RS422")
        {
            if (Item.Rs422Mode.Equals("ASCII", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Item.Rs422Ascii))
                return "Enter the RS-422 ASCII payload.";
            if (!Item.Rs422Mode.Equals("ASCII", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Item.Rs422Hex))
                return "Enter the RS-422 hexadecimal payload.";
        }
        if (type == "VDCP")
        {
            if ((Item.VdcpCommand is "CUE" or "CUE WITH DATA") && string.IsNullOrWhiteSpace(Item.VdcpClipId))
                return "VDCP CUE commands require a clip ID.";
            if (Item.VdcpCommand == "RAW HEX" && string.IsNullOrWhiteSpace(Item.VdcpHex))
                return "VDCP RAW HEX requires a complete command frame.";
        }
        if (type == "SNMP")
        {
            if (string.IsNullOrWhiteSpace(Item.SnmpHost)) return "SNMP trap destination host is required.";
            if (string.IsNullOrWhiteSpace(Item.SnmpCommunity)) return "SNMP community is required.";
            if (!LooksLikeOid(Item.SnmpTrapOid)) return "Enter a valid numeric SNMP trap OID, for example 1.3.6.1.4.1....";
            if (!LooksLikeOid(Item.SnmpVarbindOid)) return "Enter a valid numeric SNMP event varbind OID.";
        }
        return null;
    }

    private static bool LooksLikeOid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts.All(part => uint.TryParse(part, out _));
    }
    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
    private void ColorPreset_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement f && f.Tag is string color) Item.RowColor = color; }
    private void ColorAuto_Click(object s, RoutedEventArgs e) => Item.RowColor = string.Empty;
    private void TextColorPreset_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement f && f.Tag is string color) Item.RowTextColor = color; }

    private void DtmfKey_Click(object s, RoutedEventArgs e)
    {
        if (s is System.Windows.Controls.Button button && button.Content is not null) Item.DtmfSequence += button.Content.ToString();
    }
    private void DtmfClear_Click(object s, RoutedEventArgs e) => Item.DtmfSequence = "1234#";
}
