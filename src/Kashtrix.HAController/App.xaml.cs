using System;
using BroadcastPlayout.Services;
namespace Kashtrix.HAController;
public partial class App:System.Windows.Application
{
 protected override void OnStartup(System.Windows.StartupEventArgs e){base.OnStartup(e);StandaloneAppDiagnostics.Attach(this,"Kashtrix.HAController");try{var w=new MainWindow();MainWindow=w;w.Show();StandaloneAppDiagnostics.Ready("Kashtrix.HAController");if(Array.Exists(e.Args,x=>x.Equals("--startup-smoke",StringComparison.OrdinalIgnoreCase))){var t=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1200)};t.Tick+=(_,_)=>{t.Stop();w.Close();Shutdown(0);};t.Start();}}catch(Exception ex){StandaloneAppDiagnostics.Failure("Kashtrix.HAController",ex);Shutdown(-1);}}
}
