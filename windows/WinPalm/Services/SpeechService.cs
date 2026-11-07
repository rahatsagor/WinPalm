using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
namespace WinPalm.Services;

internal sealed class SpeechService : IDisposable
{
  private sealed class Session(SpeechRecognitionEngine engine, PcmAudioStream stream)
  {
    internal readonly SpeechRecognitionEngine Engine = engine;
    internal readonly PcmAudioStream Stream = stream;
    internal volatile bool Cancelled;
    internal volatile bool Stopping;
    /// Replaced by a new dictation while its final text was still coming.
    internal volatile bool Superseded;
    internal int Disposed;
  }
  private readonly ConcurrentDictionary<ulong, Session> sessions = new();
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
  internal bool Start(ulong id, string? locale)
  {
    LastStartupProblem = null;
    // A dictation that was stopped but hasn't delivered its last phrase
    // finishes alongside the new one instead of losing that phrase.
    if (sessions.TryGetValue(id, out var previous) && previous.Stopping && !previous.Cancelled && sessions.TryRemove(new(id, previous)))
      previous.Superseded = true;
    Stop(id, true);
    if (sessions.Count >= 4) return Fail(id, "All speech sessions are busy. Stop dictation on another device and try again.");
    var stage = "find a compatible offline speech language";
    try
    {
      var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
      var recognizer = recognizers.FirstOrDefault(r => string.Equals(r.Culture.Name, locale, StringComparison.OrdinalIgnoreCase))
          ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == locale?.Split('-')[0])
          ?? recognizers.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture)) ?? recognizers.FirstOrDefault();
      if (recognizer is null) return Fail(id, "No compatible offline Windows recognizer is installed. Open WinPalm → General → Windows speech settings and install an offline speech language.");
      stage = $"open the {recognizer.Culture.DisplayName} recognizer";
      var engine = new SpeechRecognitionEngine(recognizer);
      var stream = new PcmAudioStream(); var session = new Session(engine, stream);
      sessions[id] = session;
      bool Current() => !session.Cancelled && sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session);
      stage = $"load the {recognizer.Culture.DisplayName} dictation language";
      engine.LoadGrammar(new DictationGrammar());
      stage = "open the phone's audio stream";
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
      stage = "start the Windows recognizer";
      engine.RecognizeAsync(RecognizeMode.Multiple);
      // Events may race a cancellation, but the generation check above
      // prevents a previous dictation reaching the next one.
      _ = Task.Run(() => { if (Current()) Update(id, "listening", null, false); });
      return true;
    }
    catch (Exception error) when (Expected(error))
    {
      Diagnostics.Record(error); Stop(id, true);
      return Fail(id, $"Windows could not {stage}. {Code(error)} Open WinPalm → General to check speech setup and installed offline languages.");
    }
  }
  private static bool Expected(Exception error) => error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException or TypeInitializationException or DllNotFoundException or IOException;
  private static string Code(Exception error) => $"Error 0x{error.HResult:X8}.";
  private static bool Fail(ulong id, string message)
  {
    LastStartupProblem = message;
    // Queued before Start returns false, so the phone sees the actionable
    // cause before the core's generic unavailable response.
    Update(id, "error", null, false, message); return false;
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
}
