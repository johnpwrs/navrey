// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Configuration;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Reads the account out of the repo-root `.env` and applies it over settings.json.
    ///
    /// `.env` carries exactly two keys, UO_USER and UO_PASS, so credentials stay out of the
    /// tracked settings.json. Everything else the client needs - shard host and port, client
    /// version, UO data directory, POI directory, shard and character selection - is read from
    /// settings.json alone; there is deliberately no flag or env key that overrides any of it.
    /// </summary>
    internal static class AgentSettings
    {
        /// <summary>
        /// Called after settings.json has loaded. Does nothing unless agent mode was requested;
        /// the flags are read straight from <paramref name="args"/> because the normal argument
        /// pass has not run yet.
        ///
        /// -statefile / -worldfile are picked up here rather than in Main's argument switch, so
        /// those files need no field on CUOEnviroment and touch no ClassicUO file.
        /// </summary>
        public static void ApplyDotEnv(string[] args)
        {
            bool wanted = false;

            // Set by -autologin false; suppresses every path that would skip the login screen or
            // pick a character for us. Note this is deliberately not the inverse of -autologin
            // true - absent the flag entirely, .env credentials still mean "log straight in".
            bool noAutoLogin = false;
            string stateFile = null;
            string worldFile = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();

                if (arg == "-agent" || arg == "-headless")
                {
                    wanted = true;
                }
                else if (arg == "-worldfile" && i + 1 < args.Length)
                {
                    worldFile = args[i + 1];
                    wanted = true;
                }
                else if (arg == "-statefile" && i + 1 < args.Length)
                {
                    stateFile = args[i + 1];
                    wanted = true;
                }
                else if (arg == "-autologin" && i + 1 < args.Length)
                {
                    // The one switch for "let me drive the login myself". Main applies -autologin
                    // too, but only to Settings.AutoLogin and only after this pass, so it has to
                    // be read here as well: otherwise the .env pass below turns AutoLogin and
                    // SkipLoginScreen straight back on and the client drives through both the
                    // login screen and character selection regardless.
                    noAutoLogin = bool.TryParse(args[i + 1], out bool autoLogin) && !autoLogin;
                }
            }

            if (!wanted)
            {
                return;
            }

            string path = FindDotEnv();

            if (path == null)
            {
                Log.Warn("agent: no .env found at the repo root - starting without credentials");
            }
            else
            {
                Log.Trace($"agent: loading {path}");

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();

                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');

                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    // Strip a trailing inline comment, then surrounding quotes.
                    int comment = value.IndexOf(" #", StringComparison.Ordinal);

                    if (comment >= 0)
                    {
                        value = value.Substring(0, comment).Trim();
                    }

                    if (value.Length >= 2 &&
                        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    {
                        value = value.Substring(1, value.Length - 2);
                    }

                    Apply(key.ToUpperInvariant(), value, noAutoLogin);
                }
            }

            if (noAutoLogin)
            {
                // settings.json is the other source of these, and a persisted "auto_login": true
                // is enough on its own to drive past character selection - LoginScene reads
                // AutoLogin at Load() and gates both the shard pick and the character pick on it.
                // Reconnect is cleared with it because LoginScene's CanAutologin is the OR of the
                // two, so leaving it set would auto-advance just the same.
                Settings.GlobalSettings.AutoLogin = false;
                Settings.GlobalSettings.Reconnect = false;
                CUOEnviroment.SkipLoginScreen = false;
            }

            if (!string.IsNullOrEmpty(stateFile))
            {
                StateFile.Configure(stateFile);
            }

            if (!string.IsNullOrEmpty(worldFile))
            {
                WorldFile.Configure(worldFile);
            }
        }

        private static void Apply(string key, string value, bool noAutoLogin)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var settings = Settings.GlobalSettings;

            switch (key)
            {
                case "UO_USER":
                    settings.Username = value;

                    if (!noAutoLogin)
                    {
                        settings.AutoLogin = true;

                        // LoginScene only auto-connects on a first Load() when SkipLoginScreen is
                        // set; AutoLogin alone applies from the second attempt onward. Supplying
                        // credentials in .env is an explicit request to log straight in - unless
                        // -autologin false asked to stop short of that for this run.
                        CUOEnviroment.SkipLoginScreen = true;
                    }
                    break;

                case "UO_PASS":
                    // LoginScene decrypts this, so it must be stored encrypted.
                    settings.Password = Crypter.Encrypt(value);
                    break;

                default:
                    Log.Warn($"agent: ignoring {key} in .env - only UO_USER and UO_PASS are read; everything else belongs in settings.json");
                    break;
            }
        }

        /// <summary>
        /// The one place .env lives is the repo root, beside settings.json. Walks up from the
        /// working directory and the executable so a `dotnet run` from the repo root and a build
        /// under bin/Debug/net10.0 both resolve to the same file.
        /// </summary>
        private static string FindDotEnv()
        {
            foreach (string root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                var dir = new DirectoryInfo(root);

                for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, ".env");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }
}
