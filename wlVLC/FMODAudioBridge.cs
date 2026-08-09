using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace wlVLC;

public unsafe class FMODAudioBridge : IDisposable
{
    private const string MasterBusPath = "bus:/";
    private const string SfxVcaPath = "vca:/SFX";

    private const int Channels = 2;

    private const int RingSize = 1 << 20; // ~5.4s of 48kHz stereo PCM16
    private const int RingMask = RingSize - 1;

    private const float InitialLatencySeconds = 0.12f;
    private const float MinLatencySeconds = 0.06f;
    private const float MaxLatencySeconds = 2.5f;
    private const float SlackSeconds = 0.08f;
    private const long MaxLeadMicroseconds = 10_000_000L;
    private const float RoomFadeSpeed = 1f;
    private const float Headroom = 0.01f;

    private IntPtr _ring;
    private long _writePos;
    private long _readPos;
    private int _activeCallbacks;

    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly int _bytesPerSecond;
    private readonly long _minTargetBytes;
    private readonly long _maxTargetBytes;
    private readonly long _slackBytes;

    private long _targetBytes;
    private long _outputLatencyUs;
    private long _offsetUs;
    private bool _scheduleToPts;
    private int _priming = 1;

    private readonly SOUND_PCMREAD_CALLBACK _pcmReadCallback;
    private readonly SOUND_PCMSETPOS_CALLBACK _pcmSetPosCallback;

    private Sound _sound;
    private Channel _channel;
    private ChannelGroup _channelGroup;
    private bool _channelGroupIsBus;
    private bool _started;
    private bool _disposed;

    private float _roomVolume;
    private long _droppedBytes;

    public int SampleRate => _sampleRate;
    public int ChannelCount => Channels;

    public string VlcFormat => "S16N";

    public FMODAudioBridge(int requestedRate)
    {
        _sampleRate = requestedRate > 0 ? requestedRate : GetMixerSampleRate();
        _frameSize = Channels * 2;
        _bytesPerSecond = _sampleRate * _frameSize;
        _minTargetBytes = AlignToFrame((long)(_bytesPerSecond * MinLatencySeconds));
        _maxTargetBytes = AlignToFrame((long)(_bytesPerSecond * MaxLatencySeconds));
        _slackBytes = AlignToFrame((long)(_bytesPerSecond * SlackSeconds));
        _targetBytes = AlignToFrame((long)(_bytesPerSecond * InitialLatencySeconds));

        _pcmReadCallback = PcmRead;
        _pcmSetPosCallback = PcmSetPos;

        _ring = Marshal.AllocHGlobal(RingSize);
        var ring = (byte*)_ring;
        for (var i = 0; i < RingSize; i++) ring[i] = 0;
    }

    private long AlignToFrame(long bytes) => bytes - bytes % _frameSize;

    [DllImport("libvlc", EntryPoint = "libvlc_clock", CallingConvention = CallingConvention.Cdecl)]
    private static extern long LibVlcClock();

    private static bool PrimeVlcClock()
    {
        try
        {
            LibVlcClock();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("libvlc_clock is unavailable, so the cinema audio cannot be " +
                                     "scheduled against VLC's clock and may run ahead of the picture: " +
                                     ex.Message);
            return false;
        }
    }

    private static int GetMixerSampleRate()
    {
        try
        {
            if (RuntimeManager.IsInitialized &&
                RuntimeManager.CoreSystem.getSoftwareFormat(out var rate, out _, out _) == RESULT.OK && rate > 0)
            {
                return rate;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Could not query the FMOD mixer rate, defaulting to 48000: " + ex.Message);
        }

        return 48000;
    }

    public bool Start(Vector3 position, float minDistance, float maxDistance)
    {
        if (_started) return true;

        if (!RuntimeManager.IsInitialized)
        {
            Plugin.Logger.LogWarning("FMOD is not initialised yet - spatial cinema audio is not available.");
            return false;
        }

        var exinfo = new CREATESOUNDEXINFO
        {
            cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO)),
            numchannels = Channels,
            defaultfrequency = _sampleRate,
            format = SOUND_FORMAT.PCM16,
            decodebuffersize = 1024,
            // Nominal length of the looping user stream - one second of audio.
            length = (uint)(_sampleRate * _frameSize)
        };
        exinfo.pcmreadcallback = _pcmReadCallback;
        exinfo.pcmsetposcallback = _pcmSetPosCallback;

        const MODE mode = MODE.OPENUSER | MODE.CREATESTREAM | MODE.LOOP_NORMAL |
                          MODE._3D | MODE._3D_LINEARSQUAREROLLOFF;

        var result = RuntimeManager.CoreSystem.createSound(IntPtr.Zero, mode, ref exinfo, out _sound);
        if (result != RESULT.OK)
        {
            Plugin.Logger.LogError("FMOD createSound failed: " + result);
            return false;
        }

        _channelGroup = ResolveChannelGroup();

        result = RuntimeManager.CoreSystem.playSound(_sound, _channelGroup, true, out _channel);
        if (result != RESULT.OK)
        {
            Plugin.Logger.LogError("FMOD playSound failed: " + result);
            _sound.release();
            _sound.clearHandle();
            return false;
        }

        _channel.set3DMinMaxDistance(minDistance, maxDistance);
        SetChannelPosition(position);
        _channel.setVolume(0f);
        _channel.setPaused(false);

        _outputLatencyUs = MeasureOutputLatency(exinfo.decodebuffersize);
        _scheduleToPts = PrimeVlcClock();

        _started = true;
        Plugin.Logger.LogInfo($"Cinema audio routed through FMOD ({_sampleRate}Hz {VlcFormat}, " +
                              (_channelGroupIsBus ? "under bus:/" : "under the core master group") +
                              $", output latency {_outputLatencyUs / 1000f:0}ms).");
        return true;
    }

    private long MeasureOutputLatency(uint decodeBufferSize)
    {
        var latencyUs = decodeBufferSize * 1000000L / _sampleRate;

        if (RuntimeManager.CoreSystem.getDSPBufferSize(out var blockSize, out var blockCount) == RESULT.OK &&
            RuntimeManager.CoreSystem.getSoftwareFormat(out var mixerRate, out _, out _) == RESULT.OK &&
            mixerRate > 0)
        {
            latencyUs += blockSize * (long)blockCount * 1000000L / mixerRate;
        }

        return latencyUs;
    }

    public void SetLatencyOffset(float milliseconds) =>
        Interlocked.Exchange(ref _offsetUs, (long)(milliseconds * 1000f));

    private ChannelGroup ResolveChannelGroup()
    {
        try
        {
            var bus = RuntimeManager.GetBus(MasterBusPath);
            if (bus.isValid() && bus.lockChannelGroup() == RESULT.OK)
            {
                // The group only exists once Studio has processed the lock.
                RuntimeManager.StudioSystem.flushCommands();
                if (bus.getChannelGroup(out var busGroup) == RESULT.OK && busGroup.hasHandle())
                {
                    _channelGroupIsBus = true;
                    return busGroup;
                }

                bus.unlockChannelGroup();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Could not lock bus:/ for cinema audio: " + ex.Message);
        }

        RuntimeManager.CoreSystem.getMasterChannelGroup(out var masterGroup);
        return masterGroup;
    }

    public void Feed(IntPtr samples, uint frameCount, long pts)
    {
        if (samples == IntPtr.Zero || frameCount == 0) return;
        if (frameCount > (uint)_sampleRate) return;

        Interlocked.Increment(ref _activeCallbacks);
        try
        {
            if (_disposed || _ring == IntPtr.Zero) return;

            Schedule(pts);

            var count = (int)frameCount * _frameSize;
            var writePos = _writePos;
            var src = (byte*)samples;
            var dst = (byte*)_ring;
            var srcOffset = 0;

            while (count > 0)
            {
                var index = (int)(writePos & RingMask);
                var chunk = Math.Min(count, RingSize - index);
                Buffer.MemoryCopy(src + srcOffset, dst + index, RingSize - index, chunk);
                srcOffset += chunk;
                writePos += chunk;
                count -= chunk;
            }

            Interlocked.Exchange(ref _writePos, writePos);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCallbacks);
        }
    }

    private void Schedule(long pts)
    {
        if (!_scheduleToPts || pts <= 0) return;

        var lead = pts - LibVlcClock();
        if (lead >= MaxLeadMicroseconds || lead <= -MaxLeadMicroseconds) return;

        var desired = (lead - _outputLatencyUs + Interlocked.Read(ref _offsetUs)) * _sampleRate
                      / 1000000L * _frameSize;

        if (desired < _minTargetBytes) desired = _minTargetBytes;
        else if (desired > _maxTargetBytes) desired = _maxTargetBytes;

        if (Volatile.Read(ref _priming) != 0)
        {
            Interlocked.Exchange(ref _targetBytes, desired);
            return;
        }

        var current = Interlocked.Read(ref _targetBytes);
        Interlocked.Exchange(ref _targetBytes, AlignToFrame(current + (desired - current) / 32));
    }

    public void Flush()
    {
        Interlocked.Exchange(ref _writePos, Interlocked.Read(ref _readPos));
        Volatile.Write(ref _priming, 1);
    }

    private RESULT PcmRead(IntPtr sound, IntPtr data, uint datalen)
    {
        if (data == IntPtr.Zero) return RESULT.OK;

        Interlocked.Increment(ref _activeCallbacks);
        try
        {
            var needed = (int)datalen;
            var dst = (byte*)data;

            if (_disposed || _ring == IntPtr.Zero)
            {
                for (var i = 0; i < needed; i++) dst[i] = 0;
                return RESULT.OK;
            }

            var src = (byte*)_ring;
            var readPos = _readPos;
            var writePos = Interlocked.Read(ref _writePos);

            var target = Interlocked.Read(ref _targetBytes);
            var lag = writePos - readPos;
            if (lag > target + _slackBytes)
            {
                var skipped = lag - target;
                skipped -= skipped % _frameSize; // stay on a frame boundary, or L/R swap
                readPos += skipped;
                _droppedBytes += skipped;
            }

            if (Volatile.Read(ref _priming) != 0)
            {
                if (writePos - readPos < target)
                {
                    for (var i = 0; i < needed; i++) dst[i] = 0;
                    Interlocked.Exchange(ref _readPos, readPos);
                    return RESULT.OK;
                }

                Volatile.Write(ref _priming, 0);
            }

            var available = (int)Math.Min(needed, writePos - readPos);

            var written = 0;
            while (written < available)
            {
                var index = (int)(readPos & RingMask);
                var chunk = Math.Min(available - written, RingSize - index);
                Buffer.MemoryCopy(src + index, dst + written, needed - written, chunk);
                readPos += chunk;
                written += chunk;
            }

            if (written < needed) Volatile.Write(ref _priming, 1);
            for (var i = written; i < needed; i++) dst[i] = 0;

            Interlocked.Exchange(ref _readPos, readPos);
            return RESULT.OK;
        }
        finally
        {
            Interlocked.Decrement(ref _activeCallbacks);
        }
    }

    private RESULT PcmSetPos(IntPtr sound, int subsound, uint position, TIMEUNIT postype)
    {
        return RESULT.OK;
    }

    public void Update(Vector3 position, bool audible, bool occluded, float userVolume, float deltaTime)
    {
        if (!_started || _disposed) return;

        _roomVolume = Mathf.MoveTowards(_roomVolume, audible ? 1f : 0f, deltaTime * RoomFadeSpeed);

        SetChannelPosition(position);

        var sfxVolume = 1f;
        var sfx = RuntimeManager.GetVCA(SfxVcaPath);
        if (sfx.isValid()) sfx.getVolume(out sfxVolume, out _);

        _channel.setVolume(_roomVolume * sfxVolume * userVolume * Headroom);
        _channel.setLowPassGain(occluded ? 0.35f : 1f);
    }

    private void SetChannelPosition(Vector3 position)
    {
        var pos = position.ToFMODVector();
        var vel = default(VECTOR);
        if (_channel.set3DAttributes(ref pos, ref vel) == RESULT.ERR_INVALID_HANDLE)
        {
            if (RuntimeManager.CoreSystem.playSound(_sound, _channelGroup, false, out _channel) == RESULT.OK)
            {
                _channel.set3DAttributes(ref pos, ref vel);
            }
        }
    }

    public void SetMinMaxDistance(float minDistance, float maxDistance)
    {
        if (_started) _channel.set3DMinMaxDistance(minDistance, maxDistance);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            _channel.stop();
            _channel.clearHandle();

            if (_channelGroupIsBus)
            {
                var bus = RuntimeManager.GetBus(MasterBusPath);
                if (bus.isValid()) bus.unlockChannelGroup();
            }

            _channelGroup.clearHandle();
            _started = false;
        }

        if (_sound.hasHandle())
        {
            _sound.release();
            _sound.clearHandle();
        }

        for (var i = 0; i < 200 && Volatile.Read(ref _activeCallbacks) > 0; i++)
        {
            System.Threading.Thread.Sleep(1);
        }

        if (_ring != IntPtr.Zero)
        {
            var ring = _ring;
            _ring = IntPtr.Zero;
            Marshal.FreeHGlobal(ring);
        }

        var dropped = Interlocked.Read(ref _droppedBytes);
        if (dropped > 0)
        {
            Plugin.Logger.LogInfo(
                $"Cinema audio bridge closed ({dropped / (float)_bytesPerSecond:0.00}s skipped to hold latency).");
        }
    }
}
