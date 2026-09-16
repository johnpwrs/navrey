// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ClassicUO
{
    internal static class CUOEnviroment
    {
        public static Thread GameThread;
        public static float DPIScaleFactor = 1.0f;
        public static bool NoSound;
        public static string[] Args;
        public static string[] Plugins;
        public static bool Debug;
        public static bool IsHighDPI;
        public static uint CurrentRefreshRate;
        public static bool SkipLoginScreen;
        public static bool NoServerPing;

        /// <summary>Run with the game window hidden. The game loop, world and network are unchanged.</summary>
        public static bool Headless;

        /// <summary>Host the CLI command engine and its file daemon. Implied by <see cref="Headless"/>.</summary>
        public static bool Agent;

        public static string AgentCommandFile;
        public static string AgentLogFile;

        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT || Environment.OSVersion.Platform == PlatformID.Win32Windows || Environment.OSVersion.Platform == PlatformID.Win32S || Environment.OSVersion.Platform == PlatformID.WinCE;
        public static readonly bool IsUnix = !IsWindows;

        public static readonly string Version = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";
        public static readonly string ExecutablePath =
#if NETFRAMEWORK
           AppContext.BaseDirectory; // Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
#else
            Environment.CurrentDirectory;
#endif
    }
}