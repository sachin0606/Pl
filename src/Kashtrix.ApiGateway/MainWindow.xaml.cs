using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
namespace Kashtrix.ApiGateway;
public partial class MainWindow:Window
{
 private readonly GatewayHost _host=new();
 public MainWindow(){InitializeComponent();_host.Log+=x=>Dispatcher.Invoke(()=>{LogBox.AppendText(x+Environment.NewLine);LogBox.ScrollToEnd();});RefreshLabels();try{_host.Start();}catch(Exception ex){LogBox.Text="Startup failed: "+ex.Message;}}
 private void RefreshLabels(){var s=_host.Settings;HttpText.Text=$"http://{s.BindHost}:{s.HttpPort}";TcpText.Text=$"{s.BindHost}:{s.TcpPort}";UdpText.Text=$"{s.BindHost}:{s.UdpPort}";McpText.Text=$"http://{s.BindHost}:{s.HttpPort}/mcp";}
 private void Start_Click(object sender,RoutedEventArgs e){try{_host.Start();}catch(Exception ex){LogBox.AppendText("Start failed: "+ex.Message+Environment.NewLine);}}
 private void Stop_Click(object sender,RoutedEventArgs e)=>_host.Stop();
 private void OpenWeb_Click(object sender,RoutedEventArgs e){var s=_host.Settings;Process.Start(new ProcessStartInfo{FileName=$"http://{s.BindHost}:{s.HttpPort}/",UseShellExecute=true});}
 private void Window_Closing(object? sender,CancelEventArgs e)=>_host.Dispose();
}
