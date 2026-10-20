using BroadcastPlayout.Models;

namespace BroadcastPlayout.Outputs;

/// <summary>
/// Independent additional Program outputs. NDI and DeckLink/SDI routes each own a pacing
/// worker so hardware/network output never blocks the decoder or the primary Program bus.
/// </summary>
public sealed class MultiNdiOutputManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, IWorker> _workers = [];
    private bool _disposed;

    public event Action<Guid, string>? StatusChanged;

    public void Synchronize(IEnumerable<OutputRoute> routes)
    {
        if (_disposed) return;
        var wanted = routes.Where(x => x.Enabled && (x.Kind.Equals("NDI", StringComparison.OrdinalIgnoreCase) || x.Kind.Equals("DECKLINK", StringComparison.OrdinalIgnoreCase))).ToDictionary(x => x.Id);
        lock (_sync)
        {
            foreach (var id in _workers.Keys.Where(x => !wanted.ContainsKey(x)).ToArray())
            {
                _workers[id].Dispose(); _workers.Remove(id); StatusChanged?.Invoke(id, "OFF");
            }
            foreach (var pair in wanted)
            {
                var route = pair.Value; var signature = Signature(route);
                if (_workers.TryGetValue(pair.Key, out var existing) && existing.Signature == signature) continue;
                existing?.Dispose();
                IWorker worker = route.Kind.Equals("DECKLINK", StringComparison.OrdinalIgnoreCase)
                    ? new DeckLinkWorker(route, status => StatusChanged?.Invoke(route.Id, status))
                    : new NdiWorker(route, status => StatusChanged?.Invoke(route.Id, status));
                _workers[pair.Key] = worker;
            }
        }
    }

    public void SubmitVideo(VideoFrameData frame) { lock (_sync) foreach (var worker in _workers.Values) worker.SetLatest(frame); }
    public void SubmitAudio(AudioChunk chunk) { lock (_sync) foreach (var worker in _workers.Values) worker.SendAudio(chunk); }
    private static string Signature(OutputRoute r) => $"{r.Kind}|{r.SourceName}|{r.DeviceIndex}|{r.Profile.Preset}|{r.Profile.Width}|{r.Profile.Height}|{r.Profile.Scaling}|{r.FramesPerSecond:0.###}";

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; foreach (var worker in _workers.Values) worker.Dispose(); _workers.Clear(); }
    }

    private interface IWorker : IDisposable
    {
        string Signature { get; }
        void SetLatest(VideoFrameData frame);
        void SendAudio(AudioChunk chunk);
    }

    private sealed class NdiWorker : IWorker
    {
        private readonly NdiSender _sender = new(); private readonly OutputProfile _profile;
        private readonly CancellationTokenSource _cts = new(); private readonly Action<string> _status; private readonly Task _task;
        private readonly Engine.FrameMailbox _latest = new(); private bool _disposed; private string _lastStatus = "";
        public string Signature { get; }
        public NdiWorker(OutputRoute route, Action<string> status)
        {
            _status=status; _profile=route.Profile.Clone(); _profile.FramesPerSecond=route.FramesPerSecond; Signature=MultiNdiOutputManager.Signature(route);
            if(!_sender.Open(route.SourceName)){SetStatus("ERROR: "+_sender.LastError);_task=Task.CompletedTask;return;}
            SetStatus("CONNECTED"); _task=Task.Run(()=>Loop(Math.Clamp(route.FramesPerSecond,1,120),_cts.Token));
        }
        public void SetLatest(VideoFrameData frame){if(!_disposed)_latest.Post(frame);}
        public void SendAudio(AudioChunk chunk){if(_disposed||!_sender.IsOpen)return;_sender.SendAudio(chunk);if(!string.IsNullOrWhiteSpace(_sender.LastError))SetStatus("ERROR");}
        private void Loop(double fps,CancellationToken ct){var interval=TimeSpan.FromSeconds(1.0/fps);try{while(!ct.IsCancellationRequested){if(ct.WaitHandle.WaitOne(interval))return;var frame=_latest.Latest();if(frame is null)continue;_sender.SendVideo(frame,_profile);SetStatus(string.IsNullOrWhiteSpace(_sender.LastError)?"CONNECTED":"ERROR");}}catch(Exception ex){if(!ct.IsCancellationRequested)SetStatus("ERROR: "+ex.Message);}}
        private void SetStatus(string value){if(_lastStatus==value)return;_lastStatus=value;_status(value);}
        public void Dispose(){if(_disposed)return;_disposed=true;_cts.Cancel();try{_task.Wait(400);}catch{} _sender.Dispose();_cts.Dispose();}
    }

    private sealed class DeckLinkWorker : IWorker
    {
        private readonly DeckLinkOutputAdapter _output=new(); private readonly OutputProfile _profile;
        private readonly CancellationTokenSource _cts=new(); private readonly Action<string> _status; private readonly Task _task;
        private readonly Engine.FrameMailbox _latest=new(); private bool _disposed; private string _lastStatus="";
        public string Signature { get; }
        public DeckLinkWorker(OutputRoute route,Action<string> status)
        {
            _status=status; _profile=route.Profile.Clone(); _profile.FramesPerSecond=route.FramesPerSecond; _output.DeviceIndex=route.DeviceIndex; Signature=MultiNdiOutputManager.Signature(route);
            SetStatus(_output.IsSupportedBuild?"READY · SDI":"SDK INTEROP REQUIRED"); _task=Task.Run(()=>Loop(Math.Clamp(route.FramesPerSecond,1,120),_cts.Token));
        }
        public void SetLatest(VideoFrameData frame){if(!_disposed)_latest.Post(frame);}
        public void SendAudio(AudioChunk chunk){if(_disposed)return;_output.SendAudio(chunk);if(!string.IsNullOrWhiteSpace(_output.LastError)&&!_output.LastError.Contains("interop",StringComparison.OrdinalIgnoreCase))SetStatus("ERROR · "+_output.LastError);}
        private void Loop(double fps,CancellationToken ct){var interval=TimeSpan.FromSeconds(1.0/fps);try{while(!ct.IsCancellationRequested){if(ct.WaitHandle.WaitOne(interval))return;var frame=_latest.Latest();if(frame is null)continue;_output.SendVideo(frame,_profile);SetStatus(string.IsNullOrWhiteSpace(_output.LastError)?"CONNECTED · SDI":"ERROR · "+_output.LastError);}}catch(Exception ex){if(!ct.IsCancellationRequested)SetStatus("ERROR · "+ex.Message);}}
        private void SetStatus(string value){if(_lastStatus==value)return;_lastStatus=value;_status(value);}
        public void Dispose(){if(_disposed)return;_disposed=true;_cts.Cancel();try{_task.Wait(400);}catch{} _output.Dispose();_cts.Dispose();}
    }
}
