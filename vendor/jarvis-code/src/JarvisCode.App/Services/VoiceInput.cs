using System.Speech.Recognition;

namespace JarvisCode.App.Services;

/// <summary>
/// Offline dictation through the Windows speech recognizer. Recognized phrases
/// stream out via <see cref="Recognized"/>; the caller marshals to the UI thread.
/// </summary>
public sealed class VoiceInput : IDisposable
{
    private SpeechRecognitionEngine? _engine;

    public bool IsListening { get; private set; }

    public event Action<string>? Recognized;

    /// <summary>Starts dictation; returns an error message when the recognizer is unavailable.</summary>
    public string? Start()
    {
        try
        {
            if (_engine is null)
            {
                _engine = new SpeechRecognitionEngine();
                _engine.LoadGrammar(new DictationGrammar());
                _engine.SetInputToDefaultAudioDevice();
                _engine.SpeechRecognized += (_, e) =>
                {
                    if (e.Result?.Text is { Length: > 0 } text)
                    {
                        Recognized?.Invoke(text);
                    }
                };
            }

            _engine.RecognizeAsync(RecognizeMode.Multiple);
            IsListening = true;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            _engine?.Dispose();
            _engine = null;
            IsListening = false;
            return $"Voice input is unavailable: {ex.Message}";
        }
    }

    public void Stop()
    {
        try
        {
            _engine?.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException)
        {
        }

        IsListening = false;
    }

    public void Dispose()
    {
        Stop();
        _engine?.Dispose();
        _engine = null;
    }
}
