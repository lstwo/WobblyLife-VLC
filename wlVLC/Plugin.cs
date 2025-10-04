using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LibVLCSharp.Shared;
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

    private void Awake()
    {
        Logger = base.Logger;
        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll(typeof(Plugin));

        networkUrl = Config.Bind("Config", "NetworkResourceURL", "");
        useNetworkUrl = Config.Bind("Config", "UseNetworkUrl", false, "false = Use Media Path");
        optimizeForLiveStream = Config.Bind("Config", "OptimizeForLiveStream", false);
        fixedMediaPath = Config.Bind("Config", "FixedMediaPath", "");
        
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

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
        
        private void Awake()
        {
            var videoPlayer = GetComponentInChildren<VideoPlayer>();
            var wobblyCinemaPlayer = GetComponent<WobblyCinemaPlayer>();
            meshRenderer = videoPlayer.GetComponent<MeshRenderer>();
            
            //Destroy(wobblyCinemaPlayer);
            Destroy(videoPlayer);
            
            Core.Initialize();
            _libVLC = new LibVLC("--no-xlib");
            
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.SetVideoFormat("RV32", width, height, width * 4);
            _videoBuffer = new byte[width * height * 4];
            _videoBufferHandle = GCHandle.Alloc(_videoBuffer, GCHandleType.Pinned);
            
            _mediaPlayer.SetVideoCallbacks(Lock, null, Display);
            
            _videoTexture = new Texture2D((int)width, (int)height, TextureFormat.BGRA32, false);
            _previousTexture = meshRenderer.material.mainTexture;
            meshRenderer.material.mainTexture = _videoTexture;

            _isPlaying = true;

            networkUrl.SettingChanged += RefreshMedia;
            fixedMediaPath.SettingChanged += RefreshMedia;
            useNetworkUrl.SettingChanged += RefreshMedia;
            optimizeForLiveStream.SettingChanged += RefreshMedia;
            
            RefreshMedia(null, null);
        }

        private void RefreshMedia(object sender, EventArgs args)
        {
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
                    _mediaPlayer.Play(media);
                    return;
                }
                
                var networkMedia = new Media(_libVLC, networkUrl.Value, FromType.FromLocation);

                if (optimizeForLiveStream.Value)
                {
                    networkMedia.AddOption(":network-caching=50");
                    networkMedia.AddOption(":rtmp-live");
                }
                    
                _mediaPlayer.Play(networkMedia);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error refreshing media for wlVLC: " + ex);
            }
        }

        private void Update()
        {
            lock (_mainThreadActions)
            {
                while (_mainThreadActions.Count > 0)
                {
                    _mainThreadActions.Dequeue()?.Invoke();
                }
            }

            if (Input.GetKeyDown(KeyCode.Hash) || Input.GetKeyDown(KeyCode.Alpha3) || Input.inputString.Contains("#") ||
                Input.GetKeyDown(KeyCode.F9))
            {
                _isPlaying = !_isPlaying;
                meshRenderer.material.mainTexture = _isPlaying ? _videoTexture : _previousTexture;
            }
        }

        private void OnDestroy()
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();

            if (_videoBufferHandle.IsAllocated)
                _videoBufferHandle.Free();
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
    }
}