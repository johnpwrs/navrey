// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicUO.Configuration.Json;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(Settings), GenerationMode = JsonSourceGenerationMode.Metadata)]
    sealed partial class SettingsJsonContext : JsonSerializerContext
    {
        // horrible fix: https://github.com/ClassicUO/ClassicUO/issues/1663
        public static SettingsJsonContext RealDefault { get; } = new SettingsJsonContext(
            new JsonSerializerOptions()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
    }

    internal sealed class Settings
    {
        public const string SETTINGS_FILENAME = "settings.json";
        public static Settings GlobalSettings = new Settings();
        public static string CustomSettingsFilepath = null;


        [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

        [JsonPropertyName("ip")] public string IP { get; set; } = "127.0.0.1";

        [JsonPropertyName("port"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public ushort Port { get; set; } = 2593;

        /**
         * Ignores the login servers relay packet, connects back with the settings IP
         */
        [JsonPropertyName("ignore_relay_ip")] public bool IgnoreRelayIp { get; set; } = false;

        [JsonPropertyName("uo_directory")] public string UltimaOnlineDirectory { get; set; } = "";

        /// <summary>Directory of point-of-interest JSON for the agent's poi/closest/findpoi commands. Empty means the built-in data.</summary>
        [JsonPropertyName("poi_directory")] public string PoiDirectory { get; set; } = string.Empty;

        [JsonPropertyName("profiles_path")] public string ProfilesPath { get; set; } = string.Empty;

        [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = string.Empty;

        [JsonPropertyName("lang")] public string Language { get; set; } = "";

        [JsonPropertyName("last_server_num")] public ushort LastServerNum { get; set; } = 1;

        [JsonPropertyName("last_server_name")] public string LastServerName { get; set; } = string.Empty;

        [JsonPropertyName("fps")] public int FPS { get; set; } = 60;

        [JsonPropertyName("screen_scale")] public float ScreenScale { get; set; } = 1f;

        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_position")] public Point? WindowPosition { get; set; }
        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_size")] public Point? WindowSize { get; set; }

        [JsonPropertyName("is_win_maximized")] public bool IsWindowMaximized { get; set; } = true;

        [JsonPropertyName("save_account")] public bool SaveAccount { get; set; }

        [JsonPropertyName("auto_login")] public bool AutoLogin { get; set; }

        [JsonPropertyName("reconnect")] public bool Reconnect { get; set; }

        [JsonPropertyName("reconnect_time")] public int ReconnectTime { get; set; } = 1;

        [JsonPropertyName("login_music")] public bool LoginMusic { get; set; } = true;

        [JsonPropertyName("login_music_volume")] public int LoginMusicVolume { get; set; } = 70;

        [JsonPropertyName("fixed_time_step")] public bool FixedTimeStep { get; set; } = true;

        [JsonPropertyName("run_mouse_in_separate_thread")]
        public bool RunMouseInASeparateThread { get; set; } = true;

        [JsonPropertyName("force_driver")] public byte ForceDriver { get; set; }

        [JsonPropertyName("use_verdata")] public bool UseVerdata { get; set; }

        [JsonPropertyName("maps_layouts")] public string MapsLayouts { get; set; }

        [JsonPropertyName("encryption")] public byte Encryption { get; set; }

        [JsonPropertyName("plugins")] public string[] Plugins { get; set; } = { @"./Assistant/Razor.dll" };
        
        [JsonPropertyName("files_override")] public string OverrideFile { get; set; }

        public static string GetSettingsFilepath()
        {
            if (CustomSettingsFilepath != null)
            {
                if (Path.IsPathRooted(CustomSettingsFilepath))
                {
                    return CustomSettingsFilepath;
                }

                return Path.Combine(CUOEnviroment.ExecutablePath, CustomSettingsFilepath);
            }

            return Path.Combine(CUOEnviroment.ExecutablePath, SETTINGS_FILENAME);
        }


        public void Save()
        {
            // settings.json is edited by hand, never by the client. It is the one tracked source of
            // shard, version, data paths and selection, and every clean exit used to rewrite it
            // (window size, and an "auto_login": true that the .env credentials had switched on),
            // leaving the tracked file dirty after each run. Nothing the client would persist here
            // is worth that: the on-disk file stays exactly as the user wrote it.
            Log.Trace("settings.json is read-only to the client - not saving");
        }
    }
}