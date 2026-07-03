/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.

DrivableVehicles prototype changes:
- Adds .clag-style master and per-vehicle configuration files.
- Master config controls keybinds and hot-reload.
- Per-vehicle config controls top speed and optional sound file paths.
- No longer creates a top-level Sounds folder; sound folders are user-created as needed.
- Adds master-config feedback toggles for vehicle state messages.
*/

using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using System.Globalization;
using DNA.Input;
using System.IO;
using System;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    #region Main Config Loader

    /// <summary>
    /// Reads small .clag-style config files for vehicle controls, speeds, and optional sounds.
    /// </summary>
    internal static class VehicleConfig
    {
        #region Constants And Cached State

        public const string MasterConfigFileName = "DrivableVehicles.clag";
        public const string VehicleConfigFileName = "vehicle.clag";
        public const string VehicleConfigTemplateFileName = "vehicle.clag.example";

        private static readonly Dictionary<string, VehicleSettings> _vehicleSettings =
            new Dictionary<string, VehicleSettings>(StringComparer.OrdinalIgnoreCase);

        private static VehicleControls _controls = VehicleControls.Defaults();
        private static VehicleFeedback _feedback = VehicleFeedback.Defaults();

        #endregion

        #region Public Paths And Current State

        /// <summary>
        /// Root mod folder under the game's !Mods directory.
        /// </summary>
        public static string ModRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "!Mods", "DrivableVehicles");

        /// <summary>
        /// Full path to the master config file.
        /// </summary>
        public static string MasterConfigPath =>
            Path.Combine(ModRoot, MasterConfigFileName);

        /// <summary>
        /// Current master keybind settings.
        /// </summary>
        public static VehicleControls Controls
        {
            get { return _controls; }
        }

        /// <summary>
        /// Current master feedback settings.
        /// </summary>
        public static VehicleFeedback Feedback
        {
            get { return _feedback; }
        }

        /// <summary>
        /// True when non-critical spawn/enter/exit/clear messages should be shown.
        /// Errors and explicit command output are not hidden by this setting.
        /// </summary>
        public static bool VehicleStateFeedbackEnabled
        {
            get { return _feedback.Enabled && _feedback.VehicleState; }
        }
        #endregion

        #region Public Loading And Lookup API

        /// <summary>
        /// Creates default config/template files if needed and loads config values.
        /// </summary>
        public static void Initialize()
        {
            EnsureDefaultFiles();
            ReloadAll();
        }

        /// <summary>
        /// Clears caches and reloads the master config plus per-vehicle configs on demand.
        /// </summary>
        public static void ReloadAll()
        {
            _vehicleSettings.Clear();
            _controls = VehicleControls.Defaults();
            _feedback = VehicleFeedback.Defaults();

            try
            {
                EnsureDefaultFiles();
                LoadMasterConfig();
                Log("Vehicle config loaded from " + MasterConfigPath + ".");
            }
            catch (Exception ex)
            {
                Log("Failed to load vehicle config; using defaults: " + ex.Message + ".");
                _controls = VehicleControls.Defaults();
                _feedback = VehicleFeedback.Defaults();
            }
        }

        /// <summary>
        /// Returns settings for the currently selected model.
        /// </summary>
        public static VehicleSettings GetActiveVehicleSettings()
        {
            return GetVehicleSettings(VehicleContent.ActiveModelName);
        }

        /// <summary>
        /// Returns settings for a model folder, merged over defaults.
        /// </summary>
        public static VehicleSettings GetVehicleSettings(string modelName)
        {
            string key = NormalizeModelKey(modelName);
            if (string.IsNullOrWhiteSpace(key))
                key = "Alpha Prototype";

            if (_vehicleSettings.TryGetValue(key, out VehicleSettings settings) && settings != null)
                return settings;

            settings = VehicleSettings.Defaults();
            settings.ModelName = key;
            settings.ModelFolder = VehicleContent.GetModelFolderPath(key);
            settings.ConfigPath = Path.Combine(settings.ModelFolder, VehicleConfigFileName);

            try
            {
                if (File.Exists(settings.ConfigPath))
                    ApplyVehicleConfig(settings, ParseClagFile(settings.ConfigPath));
            }
            catch (Exception ex)
            {
                Log("Failed to load vehicle config for '" + key + "': " + ex.Message + ".");
            }

            _vehicleSettings[key] = settings;
            return settings;
        }

        /// <summary>
        /// Returns a short, chat-safe config summary for the selected model.
        /// </summary>
        public static string[] GetActiveConfigSummaryLines()
        {
            VehicleSettings settings = GetActiveVehicleSettings();
            return new[]
            {
                "Config: " + (File.Exists(settings.ConfigPath) ? settings.ConfigPath : "no per-vehicle config; using defaults."),
                "Speed: forward=" + settings.MaxForwardSpeed.ToString("0.###", CultureInfo.InvariantCulture) +
                    ", reverse=" + settings.MaxReverseSpeed.ToString("0.###", CultureInfo.InvariantCulture) +
                    ", accel=" + settings.Acceleration.ToString("0.###", CultureInfo.InvariantCulture) +
                    ", brake=" + settings.BrakeStrength.ToString("0.###", CultureInfo.InvariantCulture) + ".",
                "Sounds: enter=" + DisplayPath(settings.EnterSoundPath) +
                    ", accelerate=" + DisplayPath(settings.AccelerateSoundPath) +
                    ", decelerate=" + DisplayPath(settings.DecelerateSoundPath) +
                    ", skid=" + DisplayPath(settings.SkidSoundPath) + ".",
                "Feedback: vehicle state messages=" + (VehicleStateFeedbackEnabled ? "enabled." : "disabled.")
            };
        }

        /// <summary>
        /// Resolves an optional sound path against the vehicle folder first, then the mod root.
        /// </summary>
        public static string ResolveVehiclePath(VehicleSettings settings, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string trimmed = path.Trim().Trim('"', '\'');

            if (Path.IsPathRooted(trimmed))
                return trimmed;

            if (settings != null && !string.IsNullOrWhiteSpace(settings.ModelFolder))
            {
                string fromVehicleFolder = Path.GetFullPath(Path.Combine(settings.ModelFolder, trimmed));
                if (File.Exists(fromVehicleFolder))
                    return fromVehicleFolder;
            }

            return Path.GetFullPath(Path.Combine(ModRoot, trimmed));
        }
        #endregion

        #region Key Input Helpers

        /// <summary>
        /// Detects a key press for the configured hot-reload chord.
        /// </summary>
        public static bool WasReloadChordPressed(KeyboardInput keyboard)
        {
            if (keyboard == null)
                return false;

            if (!WasKeyPressed(keyboard, _controls.ReloadKey))
                return false;

            if (_controls.ReloadRequiresCtrl && !IsCtrlDown(keyboard))
                return false;

            if (_controls.ReloadRequiresShift && !IsShiftDown(keyboard))
                return false;

            if (_controls.ReloadRequiresAlt && !IsAltDown(keyboard))
                return false;

            return true;
        }

        /// <summary>
        /// Safely checks whether a configured key was pressed this frame.
        /// </summary>
        public static bool WasKeyPressed(KeyboardInput keyboard, Keys key)
        {
            if (keyboard == null || key == Keys.None)
                return false;

            try { return keyboard.WasKeyPressed(key); }
            catch { return false; }
        }

        /// <summary>
        /// Safely checks whether a configured key is currently held down.
        /// </summary>
        public static bool IsKeyDown(KeyboardInput keyboard, Keys key)
        {
            if (keyboard == null || key == Keys.None)
                return false;

            try { return keyboard.IsKeyDown(key); }
            catch { return false; }
        }

        /// <summary>
        /// Checks either Control key for hot-reload modifier support.
        /// </summary>
        private static bool IsCtrlDown(KeyboardInput keyboard)
        {
            return IsKeyDown(keyboard, Keys.LeftControl) || IsKeyDown(keyboard, Keys.RightControl);
        }

        /// <summary>
        /// Checks either Shift key for hot-reload modifier support.
        /// </summary>
        private static bool IsShiftDown(KeyboardInput keyboard)
        {
            return IsKeyDown(keyboard, Keys.LeftShift) || IsKeyDown(keyboard, Keys.RightShift);
        }

        /// <summary>
        /// Checks either Alt key for hot-reload modifier support.
        /// </summary>
        private static bool IsAltDown(KeyboardInput keyboard)
        {
            return IsKeyDown(keyboard, Keys.LeftAlt) || IsKeyDown(keyboard, Keys.RightAlt);
        }
        #endregion

        #region Config File Creation And Migration

        /// <summary>
        /// Creates the mod root, models folder, master config, and vehicle template when missing.
        /// </summary>
        /// <remarks>
        /// The top-level Sounds folder is intentionally not created; sounds live inside each vehicle folder.
        /// </remarks>
        private static void EnsureDefaultFiles()
        {
            Directory.CreateDirectory(ModRoot);
            Directory.CreateDirectory(Path.Combine(ModRoot, "Models"));

            DeleteEmptyLegacySoundsFolder();

            if (!File.Exists(MasterConfigPath))
            {
                File.WriteAllLines(MasterConfigPath, GetDefaultMasterConfigLines());
                Log("Created master config: " + MasterConfigPath + ".");
            }

            string templatePath = Path.Combine(ModRoot, VehicleConfigTemplateFileName);
            if (!File.Exists(templatePath))
            {
                File.WriteAllLines(templatePath, GetVehicleTemplateLines());
                Log("Created vehicle config template: " + templatePath + ".");
            }
        }

        /// <summary>
        /// Removes the old top-level Sounds folder only when it exists and is empty.
        /// </summary>
        private static void DeleteEmptyLegacySoundsFolder()
        {
            try
            {
                string legacySounds = Path.Combine(ModRoot, "Sounds");
                if (!Directory.Exists(legacySounds))
                    return;

                if (Directory.GetFileSystemEntries(legacySounds).Length == 0)
                {
                    Directory.Delete(legacySounds);
                    Log("Removed empty legacy sounds folder: " + legacySounds + ".");
                }
            }
            catch (Exception ex)
            {
                Log("Could not remove empty legacy sounds folder: " + ex.Message + ".");
            }
        }
        #endregion

        #region Config Loading And Applying

        /// <summary>
        /// Reads master keybind and feedback settings from DrivableVehicles.clag.
        /// </summary>
        private static void LoadMasterConfig()
        {
            Dictionary<string, Dictionary<string, string>> data = ParseClagFile(MasterConfigPath);

            if (TryGet(data, "Keys", "Enter", out string value)) _controls.EnterKey = ParseKey(value, _controls.EnterKey);
            if (TryGet(data, "Keys", "Exit", out value)) _controls.ExitKey = ParseKey(value, _controls.ExitKey);
            if (TryGet(data, "Keys", "Forward", out value)) _controls.ForwardKey = ParseKey(value, _controls.ForwardKey);
            if (TryGet(data, "Keys", "Left", out value)) _controls.LeftKey = ParseKey(value, _controls.LeftKey);
            if (TryGet(data, "Keys", "Right", out value)) _controls.RightKey = ParseKey(value, _controls.RightKey);
            if (TryGet(data, "Keys", "Reverse", out value)) _controls.ReverseKey = ParseKey(value, _controls.ReverseKey);
            if (TryGet(data, "Keys", "Brake", out value)) _controls.BrakeKey = ParseKey(value, _controls.BrakeKey);
            if (TryGet(data, "Keys", "Break", out value)) _controls.BrakeKey = ParseKey(value, _controls.BrakeKey);
            if (TryGet(data, "Keys", "Reload", out value)) _controls.ReloadKey = ParseKey(value, _controls.ReloadKey);

            if (TryGet(data, "Keys", "ReloadRequiresCtrl", out value)) _controls.ReloadRequiresCtrl = ParseBool(value, _controls.ReloadRequiresCtrl);
            if (TryGet(data, "Keys", "ReloadRequiresShift", out value)) _controls.ReloadRequiresShift = ParseBool(value, _controls.ReloadRequiresShift);
            if (TryGet(data, "Keys", "ReloadRequiresAlt", out value)) _controls.ReloadRequiresAlt = ParseBool(value, _controls.ReloadRequiresAlt);

            if (TryGet(data, "Feedback", "Enabled", out value)) _feedback.Enabled = ParseBool(value, _feedback.Enabled);
            if (TryGet(data, "Feedback", "VehicleState", out value)) _feedback.VehicleState = ParseBool(value, _feedback.VehicleState);
            if (TryGet(data, "Feedback", "State", out value)) _feedback.VehicleState = ParseBool(value, _feedback.VehicleState);
            if (TryGet(data, "Feedback", "EnterExit", out value)) _feedback.VehicleState = ParseBool(value, _feedback.VehicleState);
        }

        /// <summary>
        /// Applies per-vehicle speed and sound settings over the default vehicle settings.
        /// </summary>
        /// <remarks>
        /// Alias keys such as TopSpeed, BreakStrength, and Deaccelerate are accepted for user convenience.
        /// </remarks>
        private static void ApplyVehicleConfig(VehicleSettings settings, Dictionary<string, Dictionary<string, string>> data)
        {

            if (TryGet(data, "Vehicle", "MaxForwardSpeed", out string value)) settings.MaxForwardSpeed = ParseFloat(value, settings.MaxForwardSpeed);
            if (TryGet(data, "Vehicle", "TopSpeed", out value)) settings.MaxForwardSpeed = ParseFloat(value, settings.MaxForwardSpeed);
            if (TryGet(data, "Vehicle", "MaxReverseSpeed", out value)) settings.MaxReverseSpeed = ParseFloat(value, settings.MaxReverseSpeed);
            if (TryGet(data, "Vehicle", "ReverseSpeed", out value)) settings.MaxReverseSpeed = ParseFloat(value, settings.MaxReverseSpeed);
            if (TryGet(data, "Vehicle", "Acceleration", out value)) settings.Acceleration = ParseFloat(value, settings.Acceleration);
            if (TryGet(data, "Vehicle", "BrakeStrength", out value)) settings.BrakeStrength = ParseFloat(value, settings.BrakeStrength);
            if (TryGet(data, "Vehicle", "BreakStrength", out value)) settings.BrakeStrength = ParseFloat(value, settings.BrakeStrength);
            if (TryGet(data, "Vehicle", "Drag", out value)) settings.Drag = ParseFloat(value, settings.Drag);
            if (TryGet(data, "Vehicle", "SteerRate", out value)) settings.SteerRate = ParseFloat(value, settings.SteerRate);

            if (TryGet(data, "Sounds", "Enabled", out value)) settings.SoundsEnabled = ParseBool(value, settings.SoundsEnabled);
            if (TryGet(data, "Sounds", "Volume", out value)) settings.SoundVolume = MathHelperClamp01(ParseFloat(value, settings.SoundVolume));
            if (TryGet(data, "Sounds", "Enter", out value)) settings.EnterSoundPath = value;
            if (TryGet(data, "Sounds", "EnterCar", out value)) settings.EnterSoundPath = value;
            if (TryGet(data, "Sounds", "Accelerate", out value)) settings.AccelerateSoundPath = value;
            if (TryGet(data, "Sounds", "Acceleration", out value)) settings.AccelerateSoundPath = value;
            if (TryGet(data, "Sounds", "Decelerate", out value)) settings.DecelerateSoundPath = value;
            if (TryGet(data, "Sounds", "Deaccelerate", out value)) settings.DecelerateSoundPath = value;
            if (TryGet(data, "Sounds", "Deceleration", out value)) settings.DecelerateSoundPath = value;
            if (TryGet(data, "Sounds", "Skid", out value)) settings.SkidSoundPath = value;
            if (TryGet(data, "Sounds", "Skidding", out value)) settings.SkidSoundPath = value;

            if (settings.MaxForwardSpeed < 0f)
                settings.MaxForwardSpeed = Math.Abs(settings.MaxForwardSpeed);

            if (settings.MaxReverseSpeed > 0f)
                settings.MaxReverseSpeed = -settings.MaxReverseSpeed;

            settings.Acceleration = Math.Max(0.01f, settings.Acceleration);
            settings.BrakeStrength = Math.Max(0.01f, settings.BrakeStrength);
            settings.Drag = Math.Max(0.01f, settings.Drag);
            settings.SteerRate = Math.Max(0.01f, settings.SteerRate);
        }
        #endregion

        #region .clag Parsing And Value Helpers

        /// <summary>
        /// Parses a small sectioned key/value .clag file into a case-insensitive lookup table.
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> ParseClagFile(string path)
        {
            var data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string section = "General";

            if (!File.Exists(path))
                return data;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw == null ? string.Empty : raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("//"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]") && line.Length > 2)
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = NormalizeKey(line.Substring(0, equals));
                string value = StripInlineComment(line.Substring(equals + 1).Trim()).Trim().Trim('"', '\'');

                if (!data.TryGetValue(section, out Dictionary<string, string> sectionData))
                {
                    sectionData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    data[section] = sectionData;
                }

                sectionData[key] = value;
            }

            return data;
        }

        /// <summary>
        /// Reads a normalized key from a named .clag section.
        /// </summary>
        private static bool TryGet(Dictionary<string, Dictionary<string, string>> data, string section, string key, out string value)
        {
            value = null;

            if (!data.TryGetValue(section, out Dictionary<string, string> sectionData) || sectionData == null)
                return false;

            return sectionData.TryGetValue(NormalizeKey(key), out value);
        }

        /// <summary>
        /// Normalizes config keys so spaces, underscores, and dashes do not matter.
        /// </summary>
        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            return key.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        }

        /// <summary>
        /// Removes simple inline # or ; comments from a config value.
        /// </summary>
        private static string StripInlineComment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            int hash = value.IndexOf('#');
            int semi = value.IndexOf(';');

            int cut = -1;
            if (hash >= 0)
                cut = hash;
            if (semi >= 0 && (cut < 0 || semi < cut))
                cut = semi;

            return cut >= 0 ? value.Substring(0, cut) : value;
        }

        /// <summary>
        /// Parses a key name while preserving the existing value if parsing fails.
        /// </summary>
        private static Keys ParseKey(string value, Keys fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string safe = value.Trim();

            if (safe.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                safe.Equals("Control", StringComparison.OrdinalIgnoreCase))
                return Keys.LeftControl;

            if (safe.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                return Keys.LeftShift;

            if (safe.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                return Keys.LeftAlt;

            if (Enum.TryParse<Keys>(safe, true, out Keys parsed))
                return parsed;

            return fallback;
        }

        /// <summary>
        /// Parses common boolean text values while preserving the existing value if parsing fails.
        /// </summary>
        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string safe = value.Trim().ToLowerInvariant();
            if (safe == "1" || safe == "true" || safe == "yes" || safe == "on")
                return true;

            if (safe == "0" || safe == "false" || safe == "no" || safe == "off")
                return false;

            return fallback;
        }

        /// <summary>
        /// Parses invariant-culture floating point values with a fallback.
        /// </summary>
        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }

        /// <summary>
        /// Clamps a value into the 0..1 range used by sound volume.
        /// </summary>
        private static float MathHelperClamp01(float value)
        {
            if (value < 0f)
                return 0f;

            if (value > 1f)
                return 1f;

            return value;
        }

        /// <summary>
        /// Converts a model name or path into the safe model-folder key used by the cache.
        /// </summary>
        private static string NormalizeModelKey(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return null;

            return Path.GetFileName(modelName.Trim().Trim('"', '\''));
        }

        /// <summary>
        /// Formats optional config paths for short chat/debug output.
        /// </summary>
        private static string DisplayPath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
        }
        #endregion

        #region Default Config Templates

        /// <summary>
        /// Builds the default master config file contents for first-run setup.
        /// </summary>
        private static string[] GetDefaultMasterConfigLines()
        {
            return new[]
            {
                "# DrivableVehicles master config",
                "# Hot-reload this file in game with Ctrl+Shift+R, or use /vehicle reload.",
                "",
                "[Keys]",
                "Enter=R",
                "Exit=R",
                "Forward=W",
                "Left=A",
                "Right=D",
                "Reverse=S",
                "Brake=Space",
                "Reload=R",
                "ReloadRequiresCtrl=true",
                "ReloadRequiresShift=true",
                "ReloadRequiresAlt=false",
                "",
                "[Feedback]",
                "# VehicleState=false hides routine spawned/entered/exited/cleared messages.",
                "# Errors and explicit command output still show.",
                "VehicleState=true"
            };
        }

        /// <summary>
        /// Builds the example per-vehicle config template contents.
        /// </summary>
        private static string[] GetVehicleTemplateLines()
        {
            return new[]
            {
                "# Copy this file to a model folder as vehicle.clag.",
                "# Example: !Mods\\DrivableVehicles\\Models\\Truck\\vehicle.clag",
                "# Paths in [Sounds] are relative to that model folder first, then !Mods\\DrivableVehicles.",
                "",
                "[Vehicle]",
                "MaxForwardSpeed=14",
                "MaxReverseSpeed=-6",
                "Acceleration=16",
                "BrakeStrength=8",
                "Drag=3.5",
                "SteerRate=2.35",
                "",
                "[Sounds]",
                "Enabled=true",
                "Volume=0.75",
                "Enter=sounds\\unlock.wav",
                "Accelerate=sounds\\accel.wav",
                "Decelerate=sounds\\deaccelerate.wav",
                "Skid=sounds\\tire_skidding.wav"
            };
        }
        #endregion
    }
    #endregion

    #region Config Data Models

    /// <summary>
    /// Master feedback preferences.
    /// </summary>
    internal sealed class VehicleFeedback
    {
        #region Fields

        public bool Enabled;
        public bool VehicleState;

        #endregion


        /// <summary>
        /// Creates the default feedback preferences for a new or failed config load.
        /// </summary>
        #region Factory

        public static VehicleFeedback Defaults()
        {
            return new VehicleFeedback
            {
                Enabled = true,
                VehicleState = true
            };
        }
        #endregion
    }

    /// <summary>
    /// Master control keybinds.
    /// </summary>
    internal sealed class VehicleControls
    {
        #region Fields

        public Keys EnterKey;
        public Keys ExitKey;
        public Keys ForwardKey;
        public Keys LeftKey;
        public Keys RightKey;
        public Keys ReverseKey;
        public Keys BrakeKey;
        public Keys ReloadKey;
        public bool ReloadRequiresCtrl;
        public bool ReloadRequiresShift;
        public bool ReloadRequiresAlt;

        #endregion

        /// <summary>
        /// Creates the default keybinds for a new or failed config load.
        /// </summary>
        #region Factory

        public static VehicleControls Defaults()
        {
            return new VehicleControls
            {
                EnterKey = Keys.R,
                ExitKey = Keys.R,
                ForwardKey = Keys.W,
                LeftKey = Keys.A,
                RightKey = Keys.D,
                ReverseKey = Keys.S,
                BrakeKey = Keys.Space,
                ReloadKey = Keys.R,
                ReloadRequiresCtrl = true,
                ReloadRequiresShift = true,
                ReloadRequiresAlt = false
            };
        }
        #endregion
    }

    /// <summary>
    /// Per-vehicle driving and sound settings.
    /// </summary>
    internal sealed class VehicleSettings
    {
        #region Fields

        // Model identity and resolved file locations.
        public string ModelName;
        public string ModelFolder;
        public string ConfigPath;

        // Driving physics and steering behavior.

        public float MaxForwardSpeed;
        public float MaxReverseSpeed;
        public float Acceleration;
        public float BrakeStrength;
        public float Drag;
        public float SteerRate;

        // Optional WAV sound behavior.

        public bool SoundsEnabled;
        public float SoundVolume;
        public string EnterSoundPath;
        public string AccelerateSoundPath;
        public string DecelerateSoundPath;
        public string SkidSoundPath;

        #endregion

        /// <summary>
        /// Creates default driving and sound values for a vehicle with no vehicle.clag file.
        /// </summary>
        #region Factory

        public static VehicleSettings Defaults()
        {
            return new VehicleSettings
            {
                ModelName = "Alpha Prototype",
                ModelFolder = VehicleConfig.ModRoot,
                ConfigPath = Path.Combine(VehicleConfig.ModRoot, VehicleConfig.VehicleConfigFileName),
                MaxForwardSpeed = 14f,
                MaxReverseSpeed = -6f,
                Acceleration = 16f,
                BrakeStrength = 8f,
                Drag = 3.5f,
                SteerRate = 2.35f,
                SoundsEnabled = true,
                SoundVolume = 0.75f,
                EnterSoundPath = "sounds\\unlock.wav",
                AccelerateSoundPath = "sounds\\accel.wav",
                DecelerateSoundPath = "sounds\\deaccelerate.wav",
                SkidSoundPath = "sounds\\tire_skidding.wav"
            };
        }
        #endregion
    }
    #endregion
}