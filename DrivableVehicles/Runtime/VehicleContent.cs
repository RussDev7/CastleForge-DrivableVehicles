/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.

DrivableVehicles prototype changes:
- Loads optional vehicle model XNB files from !Mods\DrivableVehicles\Models, including per-vehicle models subfolders.
- Supports selecting different model folders with /vehicle model <name>, including names with spaces.
- Lets the original procedural placeholder be selected as Alpha Prototype.
- Writes model diagnostics to !Mods\DrivableVehicles\modeldiag.txt for easy copy/paste.
- Exposes model folder paths for per-vehicle .clag configs and optional sounds.
*/

// Organization note:
// This file is grouped by responsibility so the XNB loading flow is easier to scan:
// selection/path helpers, diagnostics, asset resolution, cached loading, and folder-rooted content access.

using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Content;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using DNA.CastleMinerZ;
using System.Text;
using System.IO;
using System;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    /// <summary>
    /// Loads vehicle XNB assets from the mod folder.
    /// </summary>
    internal static class VehicleContent
    {
        #region Constants And Cached State

        // Default selection and built-in placeholder name used when no external model is available.
        private const string DefaultModelName = "Truck";
        private const string PlaceholderModelName = "Alpha Prototype";

        private static readonly Dictionary<string, FolderContentManager> _contentManagers =
            new Dictionary<string, FolderContentManager>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ModelLoadState> _modelStates =
            new Dictionary<string, ModelLoadState>(StringComparer.OrdinalIgnoreCase);

        private static string _activeModelName = DefaultModelName;
        private static bool   _activeModelIsProceduralPlaceholder = false;

        #endregion

        #region Public Selection State And Paths

        /// <summary>
        /// Name of the model folder used for newly spawned prototype vehicles.
        /// </summary>
        public static string ActiveModelName
        {
            get { return _activeModelName; }
        }

        /// <summary>
        /// True when the selected visual is the built-in procedural placeholder.
        /// </summary>
        public static bool IsProceduralPlaceholderSelected
        {
            get { return _activeModelIsProceduralPlaceholder; }
        }

        /// <summary>
        /// Root folder where selectable vehicle model folders live.
        /// </summary>
        public static string ModelsRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "!Mods", "DrivableVehicles", "Models");

        /// <summary>
        /// Returns the folder path for a model name without requiring the XNB to exist.
        /// </summary>
        public static string GetModelFolderPath(string modelName)
        {
            string safeName = SanitizeModelName(modelName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = PlaceholderModelName;

            if (IsProceduralPlaceholderSelected && string.Equals(safeName, PlaceholderModelName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "!Mods", "DrivableVehicles");

            return Path.Combine(ModelsRoot, safeName);
        }

        /// <summary>
        /// Absolute path used by /vehicle modeldiag for easy copy/paste diagnostics.
        /// </summary>
        public static string ModelDiagnosticFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "!Mods", "DrivableVehicles", "modeldiag.txt");

        /// <summary>
        /// Returns model folder names that contain a loadable main XNB.
        /// </summary>
        public static string[] GetAvailableModelNames()
        {
            var names = new List<string>
            {
                PlaceholderModelName
            };

            try
            {
                if (!Directory.Exists(ModelsRoot))
                    return names.ToArray();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    PlaceholderModelName
                };

                foreach (string folder in Directory.GetDirectories(ModelsRoot))
                {
                    string name = Path.GetFileName(folder);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (ResolveModelAsset(name) != null && seen.Add(name))
                        names.Add(name);
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log($"Failed to list vehicle model folders: {ex.Message}.");
            }

            return names.ToArray();
        }

        /// <summary>
        /// Selects a model folder for future spawns and refreshes.
        /// </summary>
        public static bool SetActiveModelName(string name, out string message)
        {
            string safeName = SanitizeModelName(name);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                message = "Usage: /vehicle model Truck";
                return false;
            }

            // Real XNB folders intentionally take priority over the built-in placeholder aliases.
            // Example: if Models\prototype\prototype.xnb exists, /vehicle model prototype selects that XNB.
            ModelAsset asset = ResolveModelAsset(safeName);
            if (asset != null)
            {
                _activeModelName = asset.Name;
                _activeModelIsProceduralPlaceholder = false;
                message = $"Selected model '{asset.Name}'.";
                return true;
            }

            if (IsPlaceholderModelName(safeName))
            {
                _activeModelName = PlaceholderModelName;
                _activeModelIsProceduralPlaceholder = true;
                message = "Selected built-in Alpha Prototype placeholder.";
                return true;
            }

            message =
                $"Model '{safeName}' was not found. Expected {safeName}\\{safeName}.xnb, {safeName}\\models\\{safeName}.xnb, or a folder/model subfolder with exactly one .xnb under {ModelsRoot}.";
            return false;
        }

        /// <summary>
        /// Attempts to load the currently selected model.
        /// Returns false when the XNB is missing or failed to load so the procedural fallback can still draw.
        /// </summary>
        public static bool TryGetActiveModel(out Model model)
        {
            model = null;

            if (IsProceduralPlaceholderSelected)
                return false;

            return TryGetModel(_activeModelName, out model);
        }

        /// <summary>
        /// Shows the current model loading status in chat/log-friendly text.
        /// </summary>
        public static string GetActiveModelStatus()
        {
            if (IsProceduralPlaceholderSelected)
                return "Using built-in Alpha Prototype procedural placeholder. No XNB is required.";

            ModelAsset asset = ResolveModelAsset(_activeModelName);
            if (asset == null)
                return $"Model '{_activeModelName}' not found. Expected a model folder under: {ModelsRoot}";

            ModelLoadState state = GetModelState(asset);
            if (state.Model != null)
                return $"Loaded {asset.Name}/{asset.AssetName}.xnb.";

            if (state.LoadFailed)
                return $"{asset.Name}/{asset.AssetName}.xnb load failed: {state.LoadError}";

            if (File.Exists(asset.XnbPath))
                return $"{asset.Name}/{asset.AssetName}.xnb exists but has not been loaded yet: {asset.XnbPath}";

            return $"{asset.Name}/{asset.AssetName}.xnb not found. Expected: {asset.XnbPath}";
        }

        /// <summary>
        /// Returns the expected path of the currently selected model, if it can be resolved.
        /// </summary>
        public static string GetActiveModelPath()
        {
            if (IsProceduralPlaceholderSelected)
                return "<built-in procedural placeholder>";

            ModelAsset asset = ResolveModelAsset(_activeModelName);
            return asset == null
                ? Path.Combine(ModelsRoot, _activeModelName, "models", _activeModelName + ".xnb")
                : asset.XnbPath;
        }
        #endregion

        #region Diagnostics And Cache Lifecycle

        /// <summary>
        /// Returns short model summary lines safe to show in chat.
        /// </summary>
        public static string[] GetActiveModelSummaryLines()
        {
            if (IsProceduralPlaceholderSelected)
                return new[] { "Alpha Prototype is built into the mod and uses the original procedural box-car mesh." };

            TryGetActiveModel(out Model model);

            if (model == null)
                return new string[0];

            return new[]
            {
                $"{_activeModelName} contains {model.Meshes.Count} mesh(es) and {model.Bones.Count} bone(s).",
                "Use /vehicle modeldiag to log mesh names, parent bones, and mesh-part counts."
            };
        }

        /// <summary>
        /// Returns detailed model diagnostics for investigating partial FBX/XNB imports.
        /// </summary>
        public static string[] GetActiveModelDiagnosticLines()
        {
            if (IsProceduralPlaceholderSelected)
            {
                return new[]
                {
                    "Alpha Prototype diagnostics: built-in procedural placeholder.",
                    "Drawable source: generated VertexPositionColor box mesh.",
                    "XNB model: none.",
                    "Mesh count: not applicable.",
                    "Bone count: not applicable."
                };
            }

            TryGetActiveModel(out Model model);

            if (model == null)
                return new[] { GetActiveModelStatus() };

            var lines = new List<string>
            {
                $"{_activeModelName} diagnostics: {model.Meshes.Count} mesh(es), {model.Bones.Count} bone(s)."
            };

            for (int i = 0; i < model.Meshes.Count; i++)
            {
                ModelMesh mesh = model.Meshes[i];
                string meshName = string.IsNullOrEmpty(mesh.Name) ? "<unnamed>" : mesh.Name;
                string parentName = mesh.ParentBone == null || string.IsNullOrEmpty(mesh.ParentBone.Name)
                    ? "<no parent>"
                    : mesh.ParentBone.Name;

                lines.Add($"Mesh[{i}] name={meshName}, parentBone={parentName}, parts={mesh.MeshParts.Count}.");
            }

            int boneLines = 0;
            for (int i = 0; i < model.Bones.Count && boneLines < 24; i++, boneLines++)
            {
                ModelBone bone = model.Bones[i];
                string boneName = string.IsNullOrEmpty(bone.Name) ? "<unnamed>" : bone.Name;
                string parentName = bone.Parent == null || string.IsNullOrEmpty(bone.Parent.Name)
                    ? "<root>"
                    : bone.Parent.Name;

                lines.Add($"Bone[{i}] name={boneName}, parent={parentName}.");
            }

            if (model.Bones.Count > boneLines)
                lines.Add($"... {model.Bones.Count - boneLines} more bone(s) omitted from chat/log summary.");

            return lines.ToArray();
        }

        /// <summary>
        /// Writes detailed model diagnostics to !Mods\DrivableVehicles\modeldiag.txt.
        /// </summary>
        public static string WriteActiveModelDiagnosticFile(string[] diagnosticLines)
        {
            string path = ModelDiagnosticFilePath;
            string directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (diagnosticLines == null)
                diagnosticLines = GetActiveModelDiagnosticLines();

            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.WriteLine("DrivableVehicles model diagnostics");
                writer.WriteLine("Generated: " + DateTime.Now.ToString("O"));
                writer.WriteLine("Selected model: " + _activeModelName);
                writer.WriteLine("Models root: " + ModelsRoot);
                writer.WriteLine("Model path: " + GetActiveModelPath());
                writer.WriteLine("Model status: " + GetActiveModelStatus());
                writer.WriteLine();

                for (int i = 0; i < diagnosticLines.Length; i++)
                    writer.WriteLine(diagnosticLines[i]);
            }

            return path;
        }

        /// <summary>
        /// Clears cached content managers during shutdown.
        /// </summary>
        public static void Reset()
        {
            _modelStates.Clear();

            foreach (var cm in _contentManagers.Values)
            {
                try { cm?.Unload(); } catch { }
                try { (cm as IDisposable)?.Dispose(); } catch { }
            }

            _contentManagers.Clear();
        }
        #endregion

        #region Model Loading And Asset Resolution Helpers

        /// <summary>
        /// Resolves, loads, caches, configures, and diagnostics-logs a model by name.
        /// </summary>
        private static bool TryGetModel(string name, out Model model)
        {
            model = null;

            ModelAsset asset = ResolveModelAsset(name);
            if (asset == null)
                return false;

            ModelLoadState state = GetModelState(asset);
            model = state.Model;

            if (model != null)
                return true;

            if (state.LoadFailed)
                return false;

            try
            {
                var cm = GetContentManager(asset.RootFolder);

                model = cm.Load<Model>(asset.AssetName);
                state.Model = model;

                ConfigureModel(model);
                LogModelDiagnostics(model);

                Log($"Loaded vehicle model '{asset.Name}': {asset.XnbPath}.");
                return model != null;
            }
            catch (Exception ex)
            {
                state.LoadFailed = true;
                state.LoadError = ex.Message;

                Log($"Failed to load vehicle model '{asset.Name}' from {asset.XnbPath}: {ex}.");
                Log("Falling back to the procedural placeholder vehicle.");
                return false;
            }
        }

        /// <summary>
        /// Gets or creates the cached load state for a resolved model asset.
        /// </summary>
        private static ModelLoadState GetModelState(ModelAsset asset)
        {
            if (!_modelStates.TryGetValue(asset.Name, out ModelLoadState state) || state == null || !string.Equals(state.XnbPath, asset.XnbPath, StringComparison.OrdinalIgnoreCase))
            {
                state = new ModelLoadState(asset.XnbPath);
                _modelStates[asset.Name] = state;
            }

            return state;
        }

        /// <summary>
        /// Checks whether a requested model name should select the built-in Alpha Prototype fallback.
        /// </summary>
        private static bool IsPlaceholderModelName(string name)
        {
            string safeName = SanitizeModelName(name);
            if (string.IsNullOrWhiteSpace(safeName))
                return false;

            return string.Equals(safeName, PlaceholderModelName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(safeName, "Prototype", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(safeName, "Alpha", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(safeName, "Placeholder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(safeName, "Original Prototype", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(safeName, "Box Car", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a model folder name into the main XNB asset and its ContentManager root folder.
        /// </summary>
        private static ModelAsset ResolveModelAsset(string name)
        {
            string safeName = SanitizeModelName(name);
            if (string.IsNullOrWhiteSpace(safeName))
                return null;

            string vehicleFolder = Path.Combine(ModelsRoot, safeName);
            if (!Directory.Exists(vehicleFolder))
                return null;

            // Resolution priority:
            // 1. Real XNB folders win before Alpha Prototype aliases.
            // 2. Folder-name-matching XNBs win before single-XNB fallback.
            // 3. The selected content root must stay beside sidecar texture XNB dependencies.
            //
            // Support both older flat folders:
            //   Models\Truck\Truck.xnb
            // and the cleaner exported layout:
            //   Models\Truck\models\Truck.xnb
            //
            // The ContentManager root is the folder that actually contains the model XNB so
            // sidecar texture XNBs beside the model continue to resolve correctly.
            ModelAsset asset = ResolveModelAssetInContentFolder(safeName, vehicleFolder);
            if (asset != null)
                return asset;

            foreach (string contentFolder in GetModelContentSubfolders(vehicleFolder))
            {
                asset = ResolveModelAssetInContentFolder(safeName, contentFolder);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        /// <summary>
        /// Finds the best matching XNB inside one candidate content folder.
        /// </summary>
        private static ModelAsset ResolveModelAssetInContentFolder(string safeName, string contentFolder)
        {
            if (string.IsNullOrWhiteSpace(contentFolder) || !Directory.Exists(contentFolder))
                return null;

            string preferred = Path.Combine(contentFolder, safeName + ".xnb");
            if (File.Exists(preferred))
                return new ModelAsset(safeName, contentFolder, safeName, preferred);

            string[] xnbs = Directory.GetFiles(contentFolder, "*.xnb", SearchOption.TopDirectoryOnly);

            for (int i = 0; i < xnbs.Length; i++)
            {
                string assetName = Path.GetFileNameWithoutExtension(xnbs[i]);
                if (string.Equals(assetName, safeName, StringComparison.OrdinalIgnoreCase))
                    return new ModelAsset(safeName, contentFolder, assetName, xnbs[i]);
            }

            if (xnbs.Length == 1)
            {
                string assetName = Path.GetFileNameWithoutExtension(xnbs[0]);
                return new ModelAsset(safeName, contentFolder, assetName, xnbs[0]);
            }

            return null;
        }

        /// <summary>
        /// Returns supported model-content subfolders inside a vehicle folder.
        /// </summary>
        private static string[] GetModelContentSubfolders(string vehicleFolder)
        {
            var folders = new List<string>();

            AddExistingFolder(folders, Path.Combine(vehicleFolder, "models"));
            AddExistingFolder(folders, Path.Combine(vehicleFolder, "Models"));
            AddExistingFolder(folders, Path.Combine(vehicleFolder, "model"));
            AddExistingFolder(folders, Path.Combine(vehicleFolder, "Model"));

            return folders.ToArray();
        }

        /// <summary>
        /// Adds a candidate folder once, preserving case-insensitive uniqueness.
        /// </summary>
        private static void AddExistingFolder(List<string> folders, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            for (int i = 0; i < folders.Count; i++)
            {
                if (string.Equals(folders[i], path, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            folders.Add(path);
        }

        /// <summary>
        /// Converts user input into a safe folder-local model name.
        /// </summary>
        private static string SanitizeModelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            // Keep selection folder-local. Users should pass a folder name, not a path.
            // Trim quotes so both /vehicle model tofu machine and /vehicle model "tofu machine" work.
            return Path.GetFileName(name.Trim().Trim('"', '\''));
        }

        /// <summary>
        /// Gets or creates a ContentManager rooted at the folder containing the selected XNB.
        /// </summary>
        private static FolderContentManager GetContentManager(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Vehicle content root was empty.");

            if (!_contentManagers.TryGetValue(root, out FolderContentManager cm) || cm == null)
            {
                var game = CastleMinerZGame.Instance;
                if (game == null || game.Services == null)
                    throw new InvalidOperationException("Game services are not available for ContentManager.");

                cm = new FolderContentManager(game.Services, root);
                _contentManagers[root] = cm;
            }

            return cm;
        }

        /// <summary>
        /// Applies simple material settings so color-only vehicle exports render visibly in-game.
        /// </summary>
        private static void ConfigureModel(Model model)
        {
            if (model == null)
                return;

            foreach (ModelMesh mesh in model.Meshes)
            {
                foreach (Effect effect in mesh.Effects)
                {
                    if (!(effect is BasicEffect basic))
                        continue;

                    try
                    {
                        // These Unity vehicle exports are mostly material-color driven, not texture-map driven.
                        // Use unlit diffuse colors for the first prototype so bad FBX normals or missing sidecar
                        // texture references do not make most of the car appear black/invisible.
                        basic.TextureEnabled = false;
                        basic.VertexColorEnabled = false;
                        basic.LightingEnabled = false;
                        basic.Alpha = 1f;

                        if (basic.DiffuseColor.LengthSquared() < 0.0025f &&
                            basic.EmissiveColor.LengthSquared() < 0.0025f)
                        {
                            basic.DiffuseColor = new Vector3(0.08f, 0.08f, 0.08f);
                        }
                    }
                    catch
                    {
                        // Some imported effects may not accept every BasicEffect setting.
                    }
                }
            }
        }

        /// <summary>
        /// Writes selected-model mesh and bone diagnostics to the normal mod log.
        /// </summary>
        private static void LogModelDiagnostics(Model model)
        {
            if (model == null)
                return;

            foreach (string line in GetActiveModelDiagnosticLines())
                Log("" + line);
        }
        #endregion

        #region Cached Model State Containers

        /// <summary>
        /// Describes a resolved model asset and the folder used as its content root.
        /// </summary>
        private sealed class ModelAsset
        {
            public readonly string Name;
            public readonly string RootFolder;
            public readonly string AssetName;
            public readonly string XnbPath;

            /// <summary>
            /// Creates a resolved model asset descriptor.
            /// </summary>
            public ModelAsset(string name, string rootFolder, string assetName, string xnbPath)
            {
                Name = name;
                RootFolder = rootFolder;
                AssetName = assetName;
                XnbPath = xnbPath;
            }
        }

        /// <summary>
        /// Tracks the cached load result for a model XNB path.
        /// </summary>
        private sealed class ModelLoadState
        {
            public Model Model;
            public bool LoadFailed;
            public string LoadError;
            public readonly string XnbPath;

            /// <summary>
            /// Creates load state for a specific resolved XNB path.
            /// </summary>
            public ModelLoadState(string xnbPath)
            {
                XnbPath = xnbPath;
            }
        }
        #endregion

        #region Folder-Rooted XNB Content Manager

        /// <summary>
        /// ContentManager rooted at a specific on-disk folder.
        /// This lets the mod load XNB files from !Mods instead of the game's Content folder.
        /// </summary>
        private sealed class FolderContentManager : ContentManager
        {
            private readonly string _root;

            /// <summary>
            /// Creates a ContentManager that loads from one mod-owned folder.
            /// </summary>
            public FolderContentManager(IServiceProvider services, string root) : base(services)
            {
                _root = root;
            }

            /// <summary>
            /// Opens the requested asset XNB, with a sidecar fallback for dependency paths.
            /// </summary>
            protected override Stream OpenStream(string assetName)
            {
                string full = Path.Combine(_root, assetName + ".xnb");
                if (File.Exists(full))
                    return File.OpenRead(full);

                // Some converted XNBs reference dependencies by a path.
                // If all files are placed beside the main model, this fallback still resolves them.
                string sidecar = Path.Combine(_root, Path.GetFileName(assetName) + ".xnb");
                if (File.Exists(sidecar))
                    return File.OpenRead(sidecar);

                return File.OpenRead(full);
            }
        }
        #endregion
    }
}