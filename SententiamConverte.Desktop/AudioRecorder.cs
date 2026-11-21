using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SententiamConverte.Services;

namespace SententiamConverte.Desktop;

/// <summary>
/// Mikrofonaufnahme fürs Diktat (16 kHz, mono, 16 Bit, WAV): unter Windows über die Medienschnittstelle des
/// Systems, unter Linux mit „arecord“ (ALSA), unter macOS mit „rec“ (sox), falls installiert.
/// </summary>
public sealed class AudioRecorder : IAudioRecorder
{
    private string? _file;
    private Process? _process;
    private bool _recording;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    private static void Mci(string command)
    {
        var rc = mciSendString(command, null, 0, IntPtr.Zero);
        if (rc != 0) throw new InvalidOperationException($"Aufnahme nicht möglich (Fehler {rc}). Ist ein Mikrofon angeschlossen und erlaubt?");
    }

    private static string? Tool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public bool IsAvailable =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Tool("arecord") != null) || (OperatingSystem.IsMacOS() && Tool("rec") != null);

    public string Hint => OperatingSystem.IsLinux() ? "Für das Diktat „arecord“ installieren (Paket alsa-utils)."
        : OperatingSystem.IsMacOS() ? "Für das Diktat „sox“ installieren (z. B. brew install sox)." : "";

    public bool IsRecording => _recording;

    public void Start()
    {
        if (_recording) return;
        _file = Path.Combine(Path.GetTempPath(), $"sc-diktat-{Guid.NewGuid():N}.wav");
        if (OperatingSystem.IsWindows())
        {
            Mci("open new type waveaudio alias sc_diktat");
            try
            {
                Mci("set sc_diktat bitspersample 16 samplespersec 16000 channels 1");
                Mci("record sc_diktat");
            }
            catch
            {
                mciSendString("close sc_diktat", null, 0, IntPtr.Zero);
                throw;
            }
        }
        else
        {
            var (tool, args) = OperatingSystem.IsLinux()
                ? ("arecord", $"-q -f S16_LE -r 16000 -c 1 \"{_file}\"")
                : ("rec", $"-q -r 16000 -c 1 -b 16 \"{_file}\"");
            _process = Process.Start(new ProcessStartInfo(Tool(tool) ?? tool, args) { UseShellExecute = false, CreateNoWindow = true })
                       ?? throw new InvalidOperationException($"„{tool}“ ließ sich nicht starten.");
        }
        _recording = true;
    }

    public async Task<byte[]> StopAsync()
    {
        if (!_recording || _file == null) return [];
        _recording = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Mci("stop sc_diktat");
                Mci($"save sc_diktat \"{_file}\"");
                mciSendString("close sc_diktat", null, 0, IntPtr.Zero);
            }
            else if (_process is { HasExited: false } p)
            {
                // SIGINT, damit die Programme den WAV-Kopf richtig abschließen
                using var kill = Process.Start(new ProcessStartInfo("kill", $"-INT {p.Id}") { UseShellExecute = false, CreateNoWindow = true });
                await Task.Run(() => p.WaitForExit(3000));
                if (!p.HasExited) p.Kill();
            }
            return File.Exists(_file) ? await File.ReadAllBytesAsync(_file) : [];
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            try { if (File.Exists(_file)) File.Delete(_file); } catch (IOException) { }
        }
    }
}
