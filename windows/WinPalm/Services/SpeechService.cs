using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
namespace WinPalm.Services;

internal sealed class SpeechService : IDisposable
{
  private sealed class Session(SpeechRecognitionEngine engine, PcmAudioStream stream, uint? requestId)
  {
    internal readonly uint? RequestId = requestId;
    internal readonly SpeechRecognitionEngine Engine = engine;
    internal readonly PcmAudioStream Stream = stream;
    internal volatile bool Cancelled;
    internal volatile bool Stopping;
    /// Replaced by a new dictation while its final text was still coming.
    internal volatile bool Superseded;
    internal int Disposed;
  }
  private readonly ConcurrentDictionary<ulong, Session> sessions = new();
  // Include recognizers still delivering a stopped recording's last phrase.
  private readonly ConcurrentDictionary<Session, ulong> liveRecognizers = new();
  internal static string? LastStartupProblem { get; private set; }
  internal static string InstalledLanguages
  {
    get
    {
      try
      {
        var names = SpeechRecognitionEngine.InstalledRecognizers().Select(r => $"{r.Culture.DisplayName} ({r.Culture.Name})").Distinct().ToArray();
        return names.Length == 0 ? "No compatible offline Windows recognizer is installed." : "Offline speech languages: " + string.Join(", ", names) + ".";
      }
      catch (Exception error) when (Expected(error)) { return "Windows speech components could not be read. " + Code(error); }
    }
  }
  internal static bool Available
  {
    get { try { return SpeechRecognitionEngine.InstalledRecognizers().Count > 0; } catch (Exception error) when (error is PlatformNotSupportedException or System.Runtime.InteropServices.COMException or TypeInitializationException or DllNotFoundException or InvalidOperationException) { return false; } }
  }
  internal bool Start(ulong id, string? locale, uint? requestId = null)
  {
    LastStartupProblem = null;
    // A dictation that was stopped but hasn't delivered its last phrase
    // finishes alongside the new one instead of losing that phrase.
    Session? finishing = null;
    if (sessions.TryGetValue(id, out var previous) && previous.Stopping && !previous.Cancelled && sessions.TryRemove(new(id, previous)))
    {
      previous.Superseded = true; finishing = previous;
    }
    foreach (var pair in liveRecognizers.Where(pair => pair.Value == id && !ReferenceEquals(pair.Key, finishing))) Cancel(id, pair.Key);
    if (liveRecognizers.Count >= 4) return Fail("All speech sessions are busy. Stop dictation on another device and try again.");
    var stage = "find a compatible offline speech language";
    Session? created = null;
    try
    {
      var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
      var recognizer = recognizers.FirstOrDefault(r => string.Equals(r.Culture.Name, locale, StringComparison.OrdinalIgnoreCase))
          ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == locale?.Split('-')[0])
          ?? recognizers.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture)) ?? recognizers.FirstOrDefault();
      if (recognizer is null) return Fail("No compatible offline Windows recognizer is installed. Open WinPalm → General → Windows speech settings and install an offline speech language.");
      stage = $"open the {recognizer.Culture.DisplayName} recognizer";
      var engine = new SpeechRecognitionEngine(recognizer);
      var stream = new PcmAudioStream(); var session = new Session(engine, stream, requestId);
      created = session; liveRecognizers[session] = id;
      sessions[id] = session;
      bool Current() => !session.Cancelled && sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session);
      stage = $"load the {recognizer.Culture.DisplayName} dictation language";
      engine.LoadGrammar(new DictationGrammar());
      stage = "open the phone's audio stream";
      engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
      engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
      engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1200);
      engine.SpeechHypothesized += (_, e) => { if (Current()) Update(id, session.RequestId, "listening", e.Result.Text, false); };
      engine.SpeechRecognized += (_, e) => { if ((Current() || (session.Superseded && !session.Cancelled)) && e.Result.Text.Length > 0) Update(id, session.RequestId, "listening", e.Result.Text, true); };
      engine.RecognizeCompleted += (sender, e) =>
      {
        if (Current() || (session.Superseded && !session.Cancelled)) Update(id, session.RequestId, e.Error is null ? "stopped" : "error", null, false, e.Error is null ? null : "Windows speech recognition stopped. Check the installed speech language.");
        sessions.TryRemove(new(id, session));
        _ = Task.Run(() => Cleanup(session));
      };
      stage = "start the Windows recognizer";
      engine.RecognizeAsync(RecognizeMode.Multiple);
      // Events may race a cancellation, but the generation check above
      // prevents a previous dictation reaching the next one.
      _ = Task.Run(() => { if (Current()) Update(id, session.RequestId, "listening", null, false); });
      return true;
    }
    catch (Exception error) when (Expected(error))
    {
      Diagnostics.Record(error); if (created is not null) Cancel(id, created);
      return Fail($"Windows could not {stage}. {Code(error)} Open WinPalm → General to check speech setup and installed offline languages.");
    }
  }
  private static bool Expected(Exception error) => error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException or TypeInitializationException or DllNotFoundException or IOException;
  private static string Code(Exception error) => $"Error 0x{error.HResult:X8}.";
  private static bool Fail(string message)
  {
    LastStartupProblem = message;
    // Return the specific cause through the start result. A synchronous
    // callback here would reenter the core while its voice lock is held.
    return false;
  }
  internal static string CheckSetup()
  {
    var stage = "find an offline recognizer";
    try
    {
      var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
      var recognizer = recognizers.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture)) ?? recognizers.FirstOrDefault();
      if (recognizer is null) return InstalledLanguages + " Add a compatible offline speech language in Windows Settings.";
      stage = "open the recognizer";
      using var engine = new SpeechRecognitionEngine(recognizer);
      stage = "load the dictation grammar"; engine.LoadGrammar(new DictationGrammar());
      using var stream = new PcmAudioStream();
      stream.Append(new byte[3200]); stream.Complete();
      stage = "open the PCM audio stream";
      engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
      stage = "start the recognizer"; engine.RecognizeAsync(RecognizeMode.Multiple);
      return $"Speech setup passed for {recognizer.Culture.DisplayName} ({recognizer.Culture.Name}). Try dictation again from your phone over trusted HTTPS. This check uses silence and does not type anything.";
    }
    catch (Exception error) when (Expected(error))
    {
      Diagnostics.Record(error);
      return $"Windows could not {stage}. {Code(error)} Install or repair a compatible offline speech language, then restart WinPalm. Phone keyboard dictation can also enter text into the Type field.";
    }
  }
  internal void Audio(ulong id, byte[] pcm)
  {
    if (sessions.TryGetValue(id, out var session) && !session.Stopping && !session.Stream.Append(pcm))
    {
      Stop(id, true); _ = Task.Run(() => Update(id, session.RequestId, "error", null, false, "Speech could not keep up. Try again."));
    }
  }
  internal void Stop(ulong id, bool cancel)
  {
    if (cancel)
    {
      foreach (var pair in liveRecognizers.Where(pair => pair.Value == id)) Cancel(id, pair.Key);
      return;
    }
    if (!sessions.TryGetValue(id, out var session)) return;
    if (session.Stopping) return;
    session.Stopping = true;
    session.Stream.Complete();
    _ = Task.Run(async () =>
    {
      await Task.Delay(3000);
      // Cancel this exact recording; a newer one may already own the ID.
      if (Volatile.Read(ref session.Disposed) == 0) Cancel(id, session);
    });
  }
  private void Cancel(ulong id, Session session)
  {
    if (session.Cancelled || Volatile.Read(ref session.Disposed) != 0) return;
    session.Cancelled = session.Stopping = true;
    sessions.TryRemove(new(id, session)); session.Stream.Complete();
    _ = Task.Run(async () => { try { session.Engine.RecognizeAsyncCancel(); } catch (InvalidOperationException) { } finally { await Task.Delay(250); Cleanup(session); } });
  }
  private void Cleanup(Session session)
  {
    if (Interlocked.Exchange(ref session.Disposed, 1) != 0) return;
    session.Stream.Complete();
    try { session.Engine.Dispose(); }
    finally { session.Stream.Dispose(); liveRecognizers.TryRemove(session, out _); }
  }
  private static void Update(ulong id, uint? requestId, string state, string? text, bool final, string? message = null) => CoreBridge.TryCall(new { op = "voiceUpdate", id, update = new { requestId, state, text, isFinal = final, message } });
  public void Dispose() { foreach (var pair in liveRecognizers) Cancel(pair.Value, pair.Key); }
}
