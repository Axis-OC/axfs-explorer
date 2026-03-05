using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AxfsExplorer.Helpers;

sealed class SoundManager : IDisposable
{
    readonly string _soundsDir;
    readonly Random _rng = new();
    MediaPlayer? _ambientPlayer;
    string? _beep1000Path;

    public bool BeepEnabled { get; set; } = true;
    public bool GeneralEnabled { get; set; } = true;
    public double MasterVolume { get; set; } = 1.0;
    public bool BeepMuted { get; set; }
    public bool GeneralMuted { get; set; }

    double EffGen => GeneralMuted || !GeneralEnabled ? 0 : MasterVolume;
    double EffBeep => BeepMuted || !BeepEnabled ? 0 : MasterVolume;

    public SoundManager(string soundsDir)
    {
        _soundsDir = soundsDir;
        Directory.CreateDirectory(soundsDir);
    }

    // ── Sequences ──────────────────────────────────────────

    public async Task PlayImageOpenSequence()
    {
        PlayOneShot("floppy_insert.ogg", EffGen);
        await Task.Delay(300);
        PlayBeep1000(200);
        await Task.Delay(250);
        StartAmbient();
    }

    public async Task PlayErrorSequence()
    {
        StopAmbient();
        PlayBeep1000(700);
        await Task.Delay(450);
        PlayBeep1000(700);
    }

    public void PlayImageClose()
    {
        StopAmbient();
        PlayOneShot("floppy_eject.ogg", EffGen);
    }

    public void PlayHddAccess()
    {
        if (EffGen <= 0)
            return;
        int n = _rng.Next(1, 8);
        PlayOneShot($"hdd_access{n}.ogg", EffGen * 0.45);
    }

    // ── Primitives ─────────────────────────────────────────

    void PlayOneShot(string filename, double volume)
    {
        if (volume <= 0.001)
            return;
        var path = Path.Combine(_soundsDir, filename);
        if (!File.Exists(path))
            return;
        try
        {
            var p = new MediaPlayer
            {
                Volume = Math.Clamp(volume, 0, 1),
                Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path))),
            };
            p.MediaEnded += (_, _) => p.Dispose();
            p.MediaFailed += (_, _) => p.Dispose();
            p.Play();
        }
        catch { }
    }

    void PlayBeep1000(int ms)
    {
        double vol = EffBeep;
        if (vol <= 0.001)
            return;
        EnsureBeepFile();
        if (_beep1000Path == null)
            return;
        try
        {
            var p = new MediaPlayer
            {
                Volume = Math.Clamp(vol * 0.35, 0, 1),
                Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(_beep1000Path))),
            };
            p.MediaEnded += (_, _) => p.Dispose();
            p.MediaFailed += (_, _) => p.Dispose();
            p.Play();
        }
        catch { }
    }

    void EnsureBeepFile()
    {
        if (_beep1000Path != null && File.Exists(_beep1000Path))
            return;
        try
        {
            _beep1000Path = Path.Combine(Path.GetTempPath(), "axfs_beep1000.wav");
            File.WriteAllBytes(_beep1000Path, GenerateSineWav(1000, 300, 0.28));
        }
        catch
        {
            _beep1000Path = null;
        }
    }

    public void StartAmbient()
    {
        if (EffGen <= 0.001)
            return;
        StopAmbient();
        var path = Path.Combine(_soundsDir, "computer_running.ogg");
        if (!File.Exists(path))
            return;
        try
        {
            _ambientPlayer = new MediaPlayer
            {
                Volume = Math.Clamp(EffGen * 0.18, 0, 1),
                IsLoopingEnabled = true,
                Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path))),
            };
            _ambientPlayer.Play();
        }
        catch
        {
            _ambientPlayer = null;
        }
    }

    public void StopAmbient()
    {
        try
        {
            _ambientPlayer?.Pause();
            _ambientPlayer?.Dispose();
        }
        catch { }
        _ambientPlayer = null;
    }

    public void UpdateVolumes()
    {
        try
        {
            if (_ambientPlayer != null)
                _ambientPlayer.Volume = Math.Clamp(EffGen * 0.18, 0, 1);
        }
        catch { }
    }

    // ── WAV Generator ──────────────────────────────────────

    static byte[] GenerateSineWav(int hz, int durationMs, double amp = 0.28)
    {
        const int rate = 44100;
        // Pad 60ms extra on each side — MediaPlayer applies its own fade,
        // so the actual tone runs through the audible window at full level
        int padSamples = rate * 60 / 1000;
        int coreSamples = rate * durationMs / 1000;
        int totalSamples = coreSamples + 2 * padSamples;
        int dataSize = totalSamples; // 8-bit mono = 1 byte per sample

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(rate); // sample rate
        w.Write(rate); // byte rate (8-bit mono: rate * 1 * 1)
        w.Write((short)1); // block align
        w.Write((short)8); // 8 bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);

        // Exact OC algorithm:
        //   angle = 2π * offset
        //   value = (signum(sin(angle)) * amplitude).toByte ^ 0x80
        // 8-bit unsigned PCM: 128 = silence, 128±amplitude = wave
        byte amplitude = (byte)(amp * 127);
        float step = (float)hz / rate;
        float offset = 0f;

        for (int i = 0; i < totalSamples; i++)
        {
            double angle = 2.0 * Math.PI * offset;
            int sig = Math.Sign(Math.Sin(angle));
            byte value = (byte)(128 + sig * amplitude);
            w.Write(value);
            offset += step;
            if (offset > 1f)
                offset -= 1f;
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        StopAmbient();
        try
        {
            if (_beep1000Path != null)
                File.Delete(_beep1000Path);
        }
        catch { }
    }
}
