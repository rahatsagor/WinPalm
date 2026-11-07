using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
namespace WinPalm.Services;

internal sealed class SpeechService : IDisposable
{
  private sealed class Session(SpeechRecognitionEngine engine, PcmStream stream)
  {
    internal readonly SpeechRecognitionEngine Engine = engine;
    internal readonly PcmStream Stream = stream;
    internal volatile bool Cancelled;
    internal volatile bool Stopping;
    /// Replaced by a new dictation while its final text was still coming.
    internal volatile bool Superseded;
    internal int Disposed;
  }
  private readonly ConcurrentDictionary<ulong, Session> sessions = new();
  internal static bool Available
  {
    get { try { return SpeechRecognitionEngine.InstalledRecognizers().Count > 0; } catch (Exception error) when (error is PlatformNotSupportedException or System.Runtime.InteropServices.COMException or TypeInitializationException or DllNotFoundException or InvalidOperationException) { return false; } }
  }
  internal bool Start(ulong id, string? locale)
  {
    // A dictation that was stopped but hasn't delivered its last phrase
    // finishes alongside the new one instead of losing that phrase.
    if (sessions.TryGetValue(id, out var previous) && previous.Stopping && !previous.Cancelled && sessions.TryRemove(new(id, previous)))
      previous.Superseded = true;
    Stop(id, true);
    if (sessions.Count >= 4) return false;
    try
    {
      var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
      var recognizer = recognizers.FirstOrDefault(r => string.Equals(r.Culture.Name, locale, StringComparison.OrdinalIgnoreCase))
          ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == locale?.Split('-')[0])
          ?? recognizers.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture)) ?? recognizers.FirstOrDefault();
      if (recognizer is null) return false;
      var engine = new SpeechRecognitionEngine(recognizer);
      var stream = new PcmStream(); var session = new Session(engine, stream);
      sessions[id] = session;
      bool Current() => !session.Cancelled && sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session);
      engine.LoadGrammar(new DictationGrammar());
      engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
      engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
      engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1200);
      engine.SpeechHypothesized += (_, e) => { if (Current()) Update(id, "listening", e.Result.Text, false); };
      engine.SpeechRecognized += (_, e) => { if ((Current() || (session.Superseded && !session.Cancelled)) && e.Result.Text.Length > 0) Update(id, "listening", e.Result.Text, true); };
      engine.RecognizeCompleted += (sender, e) =>
      {
        if (Current()) Update(id, e.Error is null ? "stopped" : "error", null, false, e.Error is null ? null : "Windows speech recognition stopped. Check the installed speech language.");
        if (sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session)) sessions.TryRemove(id, out _);
        _ = Task.Run(() => Cleanup(session));
      };
      engine.RecognizeAsync(RecognizeMode.Multiple);
      // Events may race a cancellation, but the generation check above
      // prevents a previous dictation reaching the next one.
      _ = Task.Run(() => { if (Current()) Update(id, "listening", null, false); });
      return true;
    }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
    { Diagnostics.Record(error); Stop(id, true); return false; }
  }
  internal void Audio(ulong id, byte[] pcm)
  {
    if (sessions.TryGetValue(id, out var session) && !session.Stopping && !session.Stream.Append(pcm))
    {
      Stop(id, true); _ = Task.Run(() => Update(id, "error", null, false, "Speech could not keep up. Try again."));
    }
  }
  internal void Stop(ulong id, bool cancel)
  {
    if (!sessions.TryGetValue(id, out var session)) return;
    session.Stopping = true;
    if (cancel) { session.Cancelled = true; sessions.TryRemove(id, out _); }
    session.Stream.Complete();
    if (cancel) _ = Task.Run(async () => { try { session.Engine.RecognizeAsyncCancel(); } catch (InvalidOperationException) { } finally { await Task.Delay(250); Cleanup(session); } });
    else _ = Task.Run(async () =>
    {
      await Task.Delay(3000);
      if (sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session)) Stop(id, true);
      else if (session.Superseded && !session.Cancelled)
      {
        // The recognizer never delivered its final text.
        session.Cancelled = true;
        try { session.Engine.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
        await Task.Delay(250); Cleanup(session);
      }
    });
  }
  private static void Cleanup(Session session) { if (Interlocked.Exchange(ref session.Disposed, 1) != 0) return; session.Stream.Complete(); session.Engine.Dispose(); session.Stream.Dispose(); }
  private static void Update(ulong id, string state, string? text, bool final, string? message = null) => CoreBridge.TryCall(new { op = "voiceUpdate", id, update = new { state, text, isFinal = final, message } });
  public void Dispose() { foreach (var id in sessions.Keys) Stop(id, true); }
  /// Bounded producer/consumer stream; the recognizer owns its read thread.
  private sealed class PcmStream : Stream
  {
    private readonly BlockingCollection<byte[]> chunks = new(64); // at most ~4s
    private byte[]? current; private int offset;
    internal bool Append(byte[] pcm)
    {
      if (pcm.Length is 0 or > 32000 || (pcm.Length & 1) != 0) return false;
      try { return chunks.TryAdd(pcm); } catch (InvalidOperationException) { return false; }
    }
    internal void Complete() { if (!chunks.IsAddingCompleted) chunks.CompleteAdding(); }
    public override int Read(byte[] buffer, int start, int count)
    {
      if (count == 0) return 0;
      if (current is null || offset == current.Length)
      {
        try { current = chunks.Take(); offset = 0; } catch (InvalidOperationException) { return 0; }
      }
      var length = Math.Min(count, current.Length - offset);
      Array.Copy(current, offset, buffer, start, length); offset += length; return length;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
  }
}
