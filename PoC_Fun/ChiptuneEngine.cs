using System.IO;
using System.Reflection;
using NAudio.Wave;

namespace TrustedHive;

public sealed class ChiptuneEngine : IDisposable
{
    private readonly WaveOutEvent _waveOut;
    private readonly LoopStream _loop;
    private readonly Stream _resourceStream;
    private bool _disposed;

    public ChiptuneEngine()
    {
        _resourceStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TrustedHive.Resources.music.mp3")
            ?? throw new InvalidOperationException("Embedded music resource not found");

        var mp3 = new Mp3FileReader(_resourceStream);
        _loop = new LoopStream(mp3) { Volume = 0.55f };
        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_loop);
    }

    public float Volume
    {
        get => _loop.Volume;
        set => _loop.Volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsPlaying { get; private set; }

    public void Play()
    {
        if (_disposed) return;
        _waveOut.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        if (_disposed) return;
        _waveOut.Stop();
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _waveOut.Stop();
        _waveOut.Dispose();
        _loop.Dispose();
        _resourceStream.Dispose();
    }
}

internal sealed class LoopStream : WaveStream
{
    private readonly WaveStream _source;
    public float Volume { get; set; } = 1f;

    public LoopStream(WaveStream source) => _source = source;

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => long.MaxValue;
    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _source.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                _source.Position = 0;
                read = _source.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) break;
            }
            totalRead += read;
        }

        if (Math.Abs(Volume - 1f) > 0.001f)
        {
            int bytesPerSample = _source.WaveFormat.BitsPerSample / 8;
            if (bytesPerSample == 4)
            {
                for (int i = 0; i < totalRead; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, offset + i) * Volume;
                    BitConverter.GetBytes(sample).CopyTo(buffer, offset + i);
                }
            }
            else if (bytesPerSample == 2)
            {
                for (int i = 0; i < totalRead; i += 2)
                {
                    short sample = (short)(BitConverter.ToInt16(buffer, offset + i) * Volume);
                    BitConverter.GetBytes(sample).CopyTo(buffer, offset + i);
                }
            }
        }

        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}
