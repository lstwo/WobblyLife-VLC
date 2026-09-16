using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LibVLCSharp.Shared;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Video;

namespace wlVLC;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    internal static ConfigEntry<string> networkUrl;
    internal static ConfigEntry<bool> optimizeForLiveStream;
    internal static ConfigEntry<string> fixedMediaPath;
    internal static ConfigEntry<bool> useNetworkUrl;
    internal static ConfigEntry<bool> loopVideo;
    internal static ConfigEntry<bool> enabled;
    internal static ConfigEntry<bool> spatialAudio;
    internal static ConfigEntry<float> audioVolume;
    internal static ConfigEntry<float> audioMinDistance;
    internal static ConfigEntry<float> audioMaxDistance;
    internal static ConfigEntry<int> audioSampleRate;
    internal static ConfigEntry<float> audioOffsetMs;
    internal static ConfigEntry<bool> respectSoundRoom;
    internal static ConfigEntry<int> videoWidth;
    internal static ConfigEntry<int> videoHeight;

    private void Awake()
    {
        Logger = base.Logger;
        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll(typeof(Plugin));

        enabled = Config.Bind("Config", "Enabled", true);
        networkUrl = Config.Bind("Config", "NetworkResourceURL", "");
        useNetworkUrl = Config.Bind("Config", "UseNetworkUrl", false, "false = Use Media Path");
        optimizeForLiveStream = Config.Bind("Config", "OptimizeForLiveStream", false); 
        fixedMediaPath = Config.Bind("Config", "FixedMediaPath", "");
        loopVideo = Config.Bind("Config", "Loop", false,
            "Start the video over again when it reaches the end. Toggling this restarts playback, " +
            "and it has no effect on live streams.");
        videoWidth = Config.Bind("Config", "Video Width", 1280,
            "Resolution width for the buffer and texture, does not need to match the source video, disable and re enable to apply (e.g. 1920 for 1080p or 1280 for 720p)");
        videoHeight = Config.Bind("Config", "Video Height", 720,
            "Resolution height for the buffer and texture, does not need to match the source video, disable and re enable to apply (e.g. 1080 for 1080p or 720 for 720p)");

        spatialAudio = Config.Bind("Audio", "SpatialAudio", true,
            "Play the audio through the game's FMOD system, positioned at the cinema screen, " +
            "instead of straight out of the default audio device.");
        audioVolume = Config.Bind("Audio", "Volume", 1f,
            new ConfigDescription("Volume of the spatialized audio, on top of the game's own volume sliders. " +
                                  "1 is calibrated to sit at about the same level as the game's own sounds; " +
                                  "the range goes well above that for hosts whose media is quieter.",
                new AcceptableValueRange<float>(0f, 5f)));
        audioMinDistance = Config.Bind("Audio", "MinDistance", 5f,
            new ConfigDescription("Distance from the screen at which the audio starts getting quieter.",
                new AcceptableValueRange<float>(0.1f, 100f)));
        audioMaxDistance = Config.Bind("Audio", "MaxDistance", 45f,
            new ConfigDescription("Distance from the screen at which the audio becomes inaudible.",
                new AcceptableValueRange<float>(1f, 500f)));
        audioSampleRate = Config.Bind("Audio", "SampleRate", 0,
            "Sample rate to ask LibVLC for. 0 follows FMOD's mixer rate (48000). Setting the " +
            "media's own rate instead (usually 44100) sounds better");
        audioOffsetMs = Config.Bind("Audio", "OffsetMilliseconds", 0f,
            new ConfigDescription("Fine adjustment to when the spatialised audio plays, relative to the " +
                                  "picture. The audio is already scheduled against VLC's own clock, so " +
                                  "0 should be right; raise it if the sound still runs ahead of the " +
                                  "picture, lower it if it lags behind.",
                new AcceptableValueRange<float>(-500f, 500f)));
        respectSoundRoom = Config.Bind("Audio", "RespectSoundRoom", true,
            "Mute the audio while you are outside the cinema's sound room, the way the game " +
            "does with its own cinema soundtrack. Turn off if the audio stays silent indoors.");

        VLCWarmup.StartWarmup();

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

    [HarmonyPatch(typeof(WobblyCinemaClip), "Load")]
    [HarmonyPrefix]
    public static bool WobblyCinemaClip_Load_Prefix() => !enabled.Value || VLCWarmup.Failed;

    [HarmonyPatch(typeof(WobblyCinemaPlayer), "NetworkPost")]
    [HarmonyPostfix]
    public static void WobblyCinemaPlayer_NetworkPost_Postfix(ref WobblyCinemaPlayer __instance)
    {
        __instance.gameObject.AddComponent<VLCClient>();
    }

    [DisallowMultipleComponent]
    public class VLCClient : MonoBehaviour
    {
        private MeshRenderer meshRenderer;
        private VideoPlayer _videoPlayer;
        private WobblyCinemaPlayer _cinemaPlayer;
        private bool _warmedUp;
        private bool _setupDone;

        public uint width = 1280;
        public uint height = 720;

        private Texture2D _videoTexture;
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private GCHandle _videoBufferHandle;
        private byte[] _videoBuffer;

        private readonly Queue<Action> _mainThreadActions = new();

        private Texture _previousTexture;
        private bool _isPlaying;

        private FMODAudioBridge _audioBridge;
        private int _volumeAttempts;

        private Exception _audioCallbackFault;
        private long _audioCallbackFaults;
        private long _reportedFaults;

        private Transform _screenTransform;
        private SoundRoomData _soundRoom;

        private void Awake()
        {
            Plugin.enabled.SettingChanged += OnEnabledChanged;
            StartCoroutine(InitWhenReady());
        }

        private IEnumerator InitWhenReady()
        {
            while (!VLCWarmup.Ready)
                yield return null;

            _warmedUp = true;

            if (Plugin.enabled.Value) SetupVLCNow();
        }

        private void EnqueueOnMainThread(Action action)
        {
            lock (_mainThreadActions)
            {
                _mainThreadActions.Enqueue(action);
            }
        }

        private void OnEnabledChanged(object _, EventArgs __) => EnqueueOnMainThread(() =>
        {
            if (!_warmedUp) return;

            if (Plugin.enabled.Value) SetupVLCNow();
            else TeardownVLCNow();
        });

        private void SetupVLCNow()
        {
            if (_setupDone) return;
            _setupDone = true;

            width = (uint)math.clamp(videoWidth.Value, 1, 3840);
            height = (uint)math.clamp(videoHeight.Value, 1, 2160);

            var videoPlayer = _videoPlayer = GetComponentInChildren<VideoPlayer>();
            var wobblyCinemaPlayer = _cinemaPlayer = GetComponent<WobblyCinemaPlayer>();
            meshRenderer = videoPlayer.GetComponent<MeshRenderer>();

            _screenTransform = meshRenderer.transform;
            _soundRoom = wobblyCinemaPlayer != null ? wobblyCinemaPlayer.soundRoom : default;

            StopGameCinemaClip();

            //Destroy(wobblyCinemaPlayer);
            //Destroy(videoPlayer);
            videoPlayer.enabled = false;

            Core.Initialize();
            _libVLC = VLCWarmup.SharedVLC;

            _videoBuffer = new byte[width * height * 4];
            _videoBufferHandle = GCHandle.Alloc(_videoBuffer, GCHandleType.Pinned);

            _videoTexture = new Texture2D((int)width, (int)height, TextureFormat.BGRA32, false);
            _previousTexture = meshRenderer.material.mainTexture;
            meshRenderer.material.mainTexture = _videoTexture;

            CreateMediaPlayer();

            _isPlaying = true;

            networkUrl.SettingChanged += RefreshMedia;
            fixedMediaPath.SettingChanged += RefreshMedia;
            useNetworkUrl.SettingChanged += RefreshMedia;
            optimizeForLiveStream.SettingChanged += RefreshMedia;
            loopVideo.SettingChanged += RefreshMedia;
            spatialAudio.SettingChanged += RebuildMediaPlayer;
            audioSampleRate.SettingChanged += RebuildMediaPlayer;
            audioMinDistance.SettingChanged += RefreshAudioDistances;
            audioMaxDistance.SettingChanged += RefreshAudioDistances;
            audioOffsetMs.SettingChanged += RefreshAudioOffset;

            RefreshMediaNow();
        }

        private void TeardownVLCNow(bool restoreGameCinema = true)
        {
            if (!_setupDone) return;
            _setupDone = false;

            networkUrl.SettingChanged -= RefreshMedia;
            fixedMediaPath.SettingChanged -= RefreshMedia;
            useNetworkUrl.SettingChanged -= RefreshMedia;
            optimizeForLiveStream.SettingChanged -= RefreshMedia;
            loopVideo.SettingChanged -= RefreshMedia;
            spatialAudio.SettingChanged -= RebuildMediaPlayer;
            audioSampleRate.SettingChanged -= RebuildMediaPlayer;
            audioMinDistance.SettingChanged -= RefreshAudioDistances;
            audioMaxDistance.SettingChanged -= RefreshAudioDistances;
            audioOffsetMs.SettingChanged -= RefreshAudioOffset;

            DestroyMediaPlayer();

            if (meshRenderer != null) meshRenderer.material.mainTexture = _previousTexture;
            if (_videoPlayer != null) _videoPlayer.enabled = true;

            if (restoreGameCinema) RestartGameCinemaClip();

            meshRenderer = null;
            _isPlaying = false;

            if (_videoTexture != null) Destroy(_videoTexture);
            _videoTexture = null;

            if (_videoBufferHandle.IsAllocated) _videoBufferHandle.Free();
        }

        private void CreateMediaPlayer()
        {
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.SetVideoFormat("RV32", width, height, width * 4);
            _mediaPlayer.SetVideoCallbacks(Lock, null, Display);

            if (!spatialAudio.Value) return;

            var bridge = new FMODAudioBridge(audioSampleRate.Value);
            if (!bridge.Start(_screenTransform.position, audioMinDistance.Value, audioMaxDistance.Value))
            {
                bridge.Dispose();
                return;
            }

            bridge.SetLatencyOffset(audioOffsetMs.Value);

            _audioBridge = bridge;
            _mediaPlayer.SetAudioFormat(bridge.VlcFormat, (uint)bridge.SampleRate, (uint)bridge.ChannelCount);
            _mediaPlayer.SetAudioCallbacks(AudioPlay, null, null, AudioFlush, AudioDrain);
        }

        private void DestroyMediaPlayer()
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;

            _audioBridge?.Dispose();
            _audioBridge = null;
        }

        private void RebuildMediaPlayer(object sender, EventArgs args) => EnqueueOnMainThread(() =>
        {
            DestroyMediaPlayer();
            CreateMediaPlayer();
            RefreshMediaNow();
        });

        private void RefreshAudioDistances(object sender, EventArgs args) => EnqueueOnMainThread(() =>
            _audioBridge?.SetMinMaxDistance(audioMinDistance.Value, audioMaxDistance.Value));

        private void RefreshAudioOffset(object sender, EventArgs args) => EnqueueOnMainThread(() =>
            _audioBridge?.SetLatencyOffset(audioOffsetMs.Value));

        private WobblyCinemaClip GetCurrentCinemaClip()
        {
            if (_cinemaPlayer == null) return null;

            var clips = _cinemaPlayer.cinemaClips;
            var index = _cinemaPlayer.currentClipIndex;

            return clips != null && index < clips.Length ? clips[index] : null;
        }

        private void StopGameCinemaClip()
        {
            GetCurrentCinemaClip()?.Unload(_cinemaPlayer.videoPlayer, _cinemaPlayer.soundRoom);
        }

        private void RestartGameCinemaClip()
        {
            if (_cinemaPlayer == null || !_cinemaPlayer.bPlayingVideo) return;
            if (_cinemaPlayer.visibleTrigger == null || !_cinemaPlayer.visibleTrigger.ContainsCameras()) return;

            _cinemaPlayer.OnFirstCameraEntered_VisibleTrigger(null);
        }

        private void RefreshMedia(object sender, EventArgs args) => EnqueueOnMainThread(RefreshMediaNow);

        private void RefreshMediaNow()
        {
            if (_mediaPlayer == null) return;

            try
            {
                if ((useNetworkUrl.Value && string.IsNullOrEmpty(networkUrl.Value)) ||
                    (!useNetworkUrl.Value && string.IsNullOrEmpty(fixedMediaPath.Value)))
                {
                    _mediaPlayer.Stop();
                    return;
                }

                if (!useNetworkUrl.Value)
                {
                    var media = new Media(_libVLC, fixedMediaPath.Value);
                    ApplyLoopOption(media);
                    _mediaPlayer.Play(media);
                    return;
                }

                var networkMedia = new Media(_libVLC, networkUrl.Value, FromType.FromLocation);

                if (optimizeForLiveStream.Value)
                {
                    networkMedia.AddOption(":network-caching=50");
                    networkMedia.AddOption(":rtmp-live");
                }

                ApplyLoopOption(networkMedia);
                _mediaPlayer.Play(networkMedia);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error refreshing media for wlVLC: " + ex);
            }
        }

        private static void ApplyLoopOption(Media media)
        {
            if (loopVideo.Value) media.AddOption(":input-repeat=65535");
        }

        private void Update()
        {
            while (true)
            {
                Action action;
                lock (_mainThreadActions)
                {
                    if (_mainThreadActions.Count == 0) break;
                    action = _mainThreadActions.Dequeue();
                }

                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("wlVLC main thread action failed: " + ex);
                }
            }

            if (meshRenderer is null)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Hash) || Input.GetKeyDown(KeyCode.Alpha3) || Input.inputString.Contains("#") ||
                Input.GetKeyDown(KeyCode.F9))
            {
                _isPlaying = !_isPlaying;
                meshRenderer.material.mainTexture = _isPlaying ? _videoTexture : _previousTexture;
            }

            UpdateAudio();
        }

        private void UpdateAudio()
        {
            if (_audioBridge == null) return;

            if (_mediaPlayer != null && _volumeAttempts < 5 && _mediaPlayer.Volume != 100)
            {
                _volumeAttempts++;
                _mediaPlayer.Volume = 100;
            }

            var outsideRoom = IsListenerOutsideSoundRoom();
            var audible = _isPlaying && (!outsideRoom || _soundRoom.bOcclusion);
            var occluded = _soundRoom.bOcclusion && outsideRoom;

            _audioBridge.Update(_screenTransform.position, audible, occluded, audioVolume.Value, Time.deltaTime);

            var faults = System.Threading.Interlocked.Read(ref _audioCallbackFaults);
            if (faults > _reportedFaults)
            {
                _reportedFaults = faults;
                Logger.LogError($"[cinema audio] audio callback threw ({faults} total) - this is what " +
                                $"stops the feed: {_audioCallbackFault}");
            }
        }

        private bool IsListenerOutsideSoundRoom()
        {
            if (!respectSoundRoom.Value) return false;
            if (_soundRoom.soundRoom == SoundRoom.None) return false;
            if (!UnitySingleton<WobblySoundRoomManager>.InstanceExists) return false;

            var manager = UnitySingleton<WobblySoundRoomManager>.GetRawInstance();
            if (manager == null) return false;

            return !manager.GetCamerasInRoom().ContainsKey(_soundRoom.soundRoom);
        }

        private void OnDestroy()
        {
            Plugin.enabled.SettingChanged -= OnEnabledChanged;

            TeardownVLCNow(restoreGameCinema: false);
        }

        private IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _videoBufferHandle.AddrOfPinnedObject());
            return IntPtr.Zero;
        }

        private void Display(IntPtr opaque, IntPtr picture)
        {
            lock (_mainThreadActions)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    // A frame can still be queued up when the mod gets switched off.
                    if (_videoTexture == null) return;

                    var stride = (int)width * 4;
                    var flipped = new byte[_videoBuffer.Length];

                    for (var y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(_videoBuffer, y * stride, flipped, (_videoBuffer.Length - (y + 1) * stride), stride);
                    }

                    _videoTexture.LoadRawTextureData(flipped);
                    _videoTexture.Apply(false);
                });
            }
        }

        private void AudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            try
            {
                _audioBridge?.Feed(samples, count, pts);
            }
            catch (Exception ex)
            {
                RecordAudioFault(ex);
            }
        }

        private void AudioFlush(IntPtr data, long pts)
        {
            try
            {
                _audioBridge?.Flush();
            }
            catch (Exception ex)
            {
                RecordAudioFault(ex);
            }
        }

        private void AudioDrain(IntPtr data)
        {
            try
            {
                _audioBridge?.Flush();
            }
            catch (Exception ex)
            {
                RecordAudioFault(ex);
            }
        }

        private void RecordAudioFault(Exception ex)
        {
            _audioCallbackFault = ex;
            System.Threading.Interlocked.Increment(ref _audioCallbackFaults);
        }
    }
}

public static class VLCWarmup
{
    public static LibVLC SharedVLC;
    public static bool Ready;

    public static bool Failed;

    public static void StartWarmup()
    {
        if (Ready) return;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Core.Initialize();

                SharedVLC = new LibVLC("--no-xlib");
                Ready = true;
            }
            catch (Exception e)
            {
                Failed = true;
                Plugin.Logger.LogError("VLC warmup failed: " + e);
            }
        });
    }
}
