using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BroadcastPlayout.Views;

public partial class AuditLogWindow : Window
{
    private List<AuditRecord> _all = [];
    public ObservableCollection<AuditRecord> Rows { get; } = [];
    public AuditLogWindow(){InitializeComponent();DataContext=this;Reload();}
    private void Reload(){_all=AuditLogService.ReadRecent().ToList();ApplyFilter();}
    private void ApplyFilter(){var q=SearchBox?.Text?.Trim()??"";Rows.Clear();foreach(var r in _all.Where(r=>q.Length==0||($"{r.Action} {r.Subject} {r.Details} {r.User} {r.Machine}").Contains(q,StringComparison.OrdinalIgnoreCase)))Rows.Add(r);if(StatusText is not null)StatusText.Text=$"{Rows.Count:N0} records · {AuditLogService.CurrentPath}";}
    private void SearchBox_TextChanged(object sender,System.Windows.Controls.TextChangedEventArgs e)=>ApplyFilter();
    private void Refresh_Click(object sender,RoutedEventArgs e)=>Reload();
    private void Export_Click(object sender,RoutedEventArgs e){var d=new SaveFileDialog{Filter="CSV|*.csv",FileName=$"Kashtrix-Audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv"};if(d.ShowDialog()==true)AuditLogService.ExportCsv(d.FileName,Rows);}
    private void OpenFolder_Click(object sender,RoutedEventArgs e){Directory.CreateDirectory(AuditLogService.DirectoryPath);Process.Start(new ProcessStartInfo("explorer.exe",AuditLogService.DirectoryPath){UseShellExecute=true});}
    private void TitleBar_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)=>WindowChromeActions.Drag(this,e);
    private void Minimize_Click(object sender,RoutedEventArgs e)=>WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender,RoutedEventArgs e)=>WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
