/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.
*/

using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using DNA.Drawing.Effects;
using DNA.Drawing;
using DNA.Input;
using System;

namespace DrivableVehicles
{
    /// <summary>
    /// Simple prototype vehicle entity.
    /// 
    /// It attempts to draw the currently selected model under !Mods\DrivableVehicles\Models.
    /// If the model is missing or fails to load, it falls back to the original procedural placeholder car.
    /// </summary>
    internal sealed class PrototypeVehicleEntity : Entity
    {
        #region Fields

        // Placeholder rendering data used when no external XNB model is active.
        private readonly VertexPositionColor[] _vertices;
        private readonly short[] _indices;

        private Model _vehicleModel;
        private Matrix[] _vehicleModelTransforms;
        private ModelMesh _sourceWheelMesh;
        private int[] _clonedWheelBoneIndices;

        private BasicEffect _effect;
        private float _yaw;

        #endregion

        #region Properties

        /// <summary>
        /// Current vehicle speed in world units per second.
        /// </summary>
        public float Speed { get; set; }

        /// <summary>
        /// Vehicle yaw around world up. Positive/negative direction follows XNA's Y rotation.
        /// </summary>
        public float Yaw
        {
            get { return _yaw; }
            set
            {
                _yaw = value;
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.Up, _yaw);
            }
        }
        #endregion

        #region Construction

        /// <summary>
        /// Creates the prototype vehicle, prepares the fallback mesh, and loads the selected vehicle model.
        /// </summary>
        public PrototypeVehicleEntity()
        {
            DrawPriority = 50;
            RasterizerState = RasterizerState.CullNone;

            ReloadSelectedModel();

            BuildPlaceholderMesh(out _vertices, out _indices);
        }
        #endregion

        #region Model Loading

        /// <summary>
        /// Reloads this entity's visual model from the currently selected model folder.
        /// </summary>
        public void ReloadSelectedModel()
        {
            _vehicleModel = null;
            _vehicleModelTransforms = null;
            _sourceWheelMesh = null;
            _clonedWheelBoneIndices = null;

            if (VehicleContent.TryGetActiveModel(out Model model) && model != null)
            {
                _vehicleModel = model;
                _vehicleModelTransforms = new Matrix[_vehicleModel.Bones.Count];
                _sourceWheelMesh = FindSourceWheelMesh(_vehicleModel);
                _clonedWheelBoneIndices = FindMissingWheelBoneIndices(_vehicleModel);
            }
        }
        #endregion

        #region Driving

        /// <summary>
        /// Updates arcade-style driving controls.
        /// Speeds, steering, and keys are config-driven so each vehicle folder can tune handling.
        /// </summary>
        public void UpdateDriving(InputManager inputManager, GameTime gameTime)
        {
            if (inputManager == null || gameTime == null)
                return;

            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (dt <= 0f)
                return;

            if (dt > 0.1f)
                dt = 0.1f;

            var keyboard = inputManager.Keyboard;
            VehicleControls controls = VehicleConfig.Controls;
            VehicleSettings settings = VehicleConfig.GetActiveVehicleSettings();

            float maxForwardSpeed = settings == null ? 14f : settings.MaxForwardSpeed;
            float maxReverseSpeed = settings == null ? -6f : settings.MaxReverseSpeed;
            float acceleration = settings == null ? 16f : settings.Acceleration;
            float brakeStrength = settings == null ? 8f : settings.BrakeStrength;
            float drag = settings == null ? 3.5f : settings.Drag;
            float steerRate = settings == null ? 2.35f : settings.SteerRate;

            bool forwardKey = VehicleConfig.IsKeyDown(keyboard, controls.ForwardKey);
            bool reverseKey = VehicleConfig.IsKeyDown(keyboard, controls.ReverseKey);
            bool brakeKey = VehicleConfig.IsKeyDown(keyboard, controls.BrakeKey);
            bool leftKey = VehicleConfig.IsKeyDown(keyboard, controls.LeftKey);
            bool rightKey = VehicleConfig.IsKeyDown(keyboard, controls.RightKey);

            float throttle = 0f;
            if (forwardKey)
                throttle += 1f;
            if (reverseKey)
                throttle -= 0.65f;

            if (brakeKey)
            {
                Speed = Approach(Speed, 0f, brakeStrength * dt);
            }
            else if (Math.Abs(throttle) > 0.001f)
            {
                Speed += throttle * acceleration * dt;
            }
            else
            {
                Speed = Approach(Speed, 0f, drag * dt);
            }

            Speed = MathHelper.Clamp(Speed, maxReverseSpeed, maxForwardSpeed);

            float steerInput = 0f;
            if (leftKey)
                steerInput -= 1f;
            if (rightKey)
                steerInput += 1f;

            if (Math.Abs(steerInput) > 0.001f && Math.Abs(Speed) > 0.05f)
            {
                float speedFactor = MathHelper.Clamp(Math.Abs(Speed) / Math.Max(0.001f, maxForwardSpeed), 0.15f, 1f);
                float direction = Speed >= 0f ? 1f : -1f;

                Yaw -= steerInput * steerRate * speedFactor * direction * dt;
            }

            Vector3 forward = Vector3.Transform(Vector3.Forward, Matrix.CreateRotationY(Yaw));
            Vector3 next = LocalPosition + forward * Speed * dt;
            LocalPosition = VehicleRuntime.SnapToGround(next);

            VehicleAudio.UpdateDriving(forwardKey, reverseKey, brakeKey, Math.Abs(steerInput) > 0.001f, Speed);
        }
        #endregion

        #region Seat Position

        /// <summary>
        /// Approximate driver seat position in world space.
        /// </summary>
        public Vector3 GetSeatWorldPosition()
        {
            Vector3 localSeat = new Vector3(0f, 1.25f, -0.35f);
            return LocalPosition + Vector3.Transform(localSeat, Matrix.CreateRotationY(Yaw));
        }
        #endregion

        #region Rendering

        /// <summary>
        /// Draws the selected XNB vehicle model, or the procedural placeholder when no model is available.
        /// </summary>
        public override void Draw(GraphicsDevice device, GameTime gameTime, Matrix view, Matrix projection)
        {
            if (device == null)
                return;

            if (_vehicleModel != null && _vehicleModelTransforms != null)
            {
                DrawVehicleModel(gameTime, view, projection);
                base.Draw(device, gameTime, view, projection);
                return;
            }

            DrawPlaceholder(device, view, projection);
            base.Draw(device, gameTime, view, projection);
        }

        /// <summary>
        /// Draws the loaded vehicle model using the current scale, yaw offset, and vertical model offset.
        /// </summary>
        private void DrawVehicleModel(GameTime gameTime, Matrix view, Matrix projection)
        {
            _vehicleModel.CopyAbsoluteBoneTransformsTo(_vehicleModelTransforms);

            Matrix visualWorld =
                Matrix.CreateScale(VehicleRuntime.ModelScale) *
                Matrix.CreateRotationY(VehicleRuntime.ModelYawOffsetRadians) *
                Matrix.CreateTranslation(0f, VehicleRuntime.ModelYOffset, 0f) *
                LocalToWorld;

            foreach (ModelMesh mesh in _vehicleModel.Meshes)
                DrawModelMeshAt(mesh, _vehicleModelTransforms[mesh.ParentBone.Index] * visualWorld, gameTime, view, projection);

            DrawClonedMissingWheels(gameTime, view, projection, visualWorld);
        }

        /// <summary>
        /// Draws cloned wheel meshes at missing wheel bone locations when the importer only produced one wheel mesh.
        /// </summary>
        private void DrawClonedMissingWheels(GameTime gameTime, Matrix view, Matrix projection, Matrix visualWorld)
        {
            if (!VehicleRuntime.CloneMissingWheels || _sourceWheelMesh == null || _clonedWheelBoneIndices == null)
                return;

            for (int i = 0; i < _clonedWheelBoneIndices.Length; i++)
            {
                int boneIndex = _clonedWheelBoneIndices[i];
                if (boneIndex < 0 || boneIndex >= _vehicleModelTransforms.Length)
                    continue;

                DrawModelMeshAt(_sourceWheelMesh, _vehicleModelTransforms[boneIndex] * visualWorld, gameTime, view, projection);
            }
        }

        /// <summary>
        /// Applies the active world/view/projection matrices to one mesh and draws it.
        /// </summary>
        private static void DrawModelMeshAt(ModelMesh mesh, Matrix world, GameTime gameTime, Matrix view, Matrix projection)
        {
            foreach (Effect effect in mesh.Effects)
                ApplyModelEffect(effect, gameTime, world, view, projection);

            mesh.Draw();
        }
        #endregion

        #region Wheel Clone Helpers

        /// <summary>
        /// Finds the best wheel mesh to reuse when exported XNB models contain only one drawable wheel.
        /// </summary>
        private static ModelMesh FindSourceWheelMesh(Model model)
        {
            if (model == null)
                return null;

            // Prefer the front-left wheel mesh. Unity/Blender/XNB names commonly become
            // Wheel_1, Wheel_1.002, etc. Matching by prefix keeps this working across
            // several exported vehicles without per-car hardcoding.
            foreach (ModelMesh mesh in model.Meshes)
            {
                if (IsNamedWheel(mesh.Name, "Wheel_1") ||
                    (mesh.ParentBone != null && IsNamedWheel(mesh.ParentBone.Name, "Wheel_1")))
                    return mesh;
            }

            // Fallback: use the first drawable mesh parented to a wheel leaf bone.
            foreach (ModelMesh mesh in model.Meshes)
            {
                if (mesh.ParentBone != null && IsWheelBoneName(mesh.ParentBone.Name) && IsLeafWheelBone(mesh.ParentBone))
                    return mesh;
            }

            // Last resort: use the first mesh whose own name looks like a wheel.
            foreach (ModelMesh mesh in model.Meshes)
            {
                if (IsWheelBoneName(mesh.Name))
                    return mesh;
            }

            return null;
        }

        /// <summary>
        /// Finds wheel leaf bones that do not already have a drawable mesh attached.
        /// </summary>
        private static int[] FindMissingWheelBoneIndices(Model model)
        {
            if (model == null)
                return new int[0];

            var meshWheelBoneIndices = new List<int>();
            foreach (ModelMesh mesh in model.Meshes)
            {
                if (mesh.ParentBone != null && IsWheelBoneName(mesh.ParentBone.Name))
                    meshWheelBoneIndices.Add(mesh.ParentBone.Index);
            }

            var indices = new List<int>();

            foreach (ModelBone bone in model.Bones)
            {
                if (!IsWheelBoneName(bone.Name))
                    continue;

                // Skip helper/parent wheel nodes such as Wheel.002 or Wheel_2.002.
                // We only want the final leaf wheel bones like Wheel_3.002, Wheel_5.002,
                // Wheel_6, or Wheel_7.001 depending on the export.
                if (!IsLeafWheelBone(bone))
                    continue;

                // If the content pipeline eventually imports this wheel as its own mesh,
                // let the normal model draw path handle it and do not duplicate it here.
                if (ContainsIndex(meshWheelBoneIndices, bone.Index))
                    continue;

                indices.Add(bone.Index);
            }

            return indices.ToArray();
        }

        /// <summary>
        /// Checks whether a bone index is already represented by an imported mesh.
        /// </summary>
        private static bool ContainsIndex(List<int> indices, int value)
        {
            if (indices == null)
                return false;

            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i] == value)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when a wheel bone has no child wheel bone and can be used as a final wheel target.
        /// </summary>
        private static bool IsLeafWheelBone(ModelBone bone)
        {
            if (bone == null)
                return false;

            foreach (ModelBone child in bone.Children)
            {
                if (IsWheelBoneName(child.Name))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Matches a specific wheel prefix while allowing Blender/FBX suffixes such as .002 or _001.
        /// </summary>
        private static bool IsNamedWheel(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix))
                return false;

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (name.Length == prefix.Length)
                return true;

            char next = name[prefix.Length];

            // Accept Blender/FBX suffixes like Wheel_1.002 and generated forms like Wheel_1_001.
            return next == '.' || next == '_' || next == '-';
        }

        /// <summary>
        /// Identifies wheel-related bone names while excluding helper effects such as WheelSpinFx.
        /// </summary>
        private static bool IsWheelBoneName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.StartsWith("WheelSpin", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!name.StartsWith("Wheel", StringComparison.OrdinalIgnoreCase))
                return false;

            if (name.Length == "Wheel".Length)
                return true;

            char next = name["Wheel".Length];

            // Accept Wheel_1, Wheel_1.002, Wheel__1_, Wheel.002, Wheel-001.
            // Reject unrelated names such as WheelSpinFx.
            return next == '_' || next == '.' || next == '-';
        }
        #endregion

        #region Effect Helpers

        /// <summary>
        /// Applies standard matrix/time parameters to imported model effects before drawing.
        /// </summary>
        private static void ApplyModelEffect(Effect effect, GameTime gameTime, Matrix world, Matrix view, Matrix projection)
        {
            if (effect is IEffectMatrices matrices)
            {
                matrices.World = world;
                matrices.View = view;
                matrices.Projection = projection;
            }
            else
            {
                TrySetMatrix(effect, "World", world);
                TrySetMatrix(effect, "View", view);
                TrySetMatrix(effect, "Projection", projection);
            }

            if (effect is IEffectTime time && gameTime != null)
            {
                time.ElaspedTime = gameTime.ElapsedGameTime;
                time.TotalTime = gameTime.TotalGameTime;
            }
        }

        /// <summary>
        /// Best-effort matrix assignment for custom effects that expose matrix parameters by name.
        /// </summary>
        private static void TrySetMatrix(Effect effect, string name, Matrix value)
        {
            try
            {
                EffectParameter parameter = effect.Parameters[name];
                parameter?.SetValue(value);
            }
            catch
            {
                // Best-effort fallback for custom effects.
            }
        }
        #endregion

        #region Placeholder Rendering

        /// <summary>
        /// Draws the built-in alpha prototype car when no external XNB model is active.
        /// </summary>
        private void DrawPlaceholder(GraphicsDevice device, Matrix view, Matrix projection)
        {
            if (_vertices == null || _indices == null)
                return;

            if (_effect == null)
            {
                _effect = new BasicEffect(device)
                {
                    VertexColorEnabled = true,
                    LightingEnabled = false
                };
            }

            _effect.World = LocalToWorld;
            _effect.View = view;
            _effect.Projection = projection;

            foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    _vertices,
                    0,
                    _vertices.Length,
                    _indices,
                    0,
                    _indices.Length / 3);
            }
        }
        #endregion

        #region Math Helpers

        /// <summary>
        /// Moves a value toward a target by a fixed amount without overshooting.
        /// </summary>
        private static float Approach(float value, float target, float amount)
        {
            if (value < target)
                return Math.Min(value + amount, target);

            if (value > target)
                return Math.Max(value - amount, target);

            return value;
        }
        #endregion

        #region Placeholder Mesh Builders

        /// <summary>
        /// Builds the simple colored box-car mesh used by the alpha prototype fallback.
        /// </summary>
        private static void BuildPlaceholderMesh(out VertexPositionColor[] vertices, out short[] indices)
        {
            var vertexList = new List<VertexPositionColor>();
            var indexList = new List<short>();

            AddBox(vertexList, indexList, new Vector3(0f, 0.55f, 0f),    new Vector3(2.0f, 0.65f, 3.4f), new Color(70, 120, 220));
            AddBox(vertexList, indexList, new Vector3(0f, 1.05f, -0.35f), new Vector3(1.25f, 0.65f, 1.35f), new Color(95, 145, 235));

            AddBox(vertexList, indexList, new Vector3(-1.12f, 0.35f, -1.15f), new Vector3(0.35f, 0.55f, 0.65f), Color.Black);
            AddBox(vertexList, indexList, new Vector3( 1.12f, 0.35f, -1.15f), new Vector3(0.35f, 0.55f, 0.65f), Color.Black);
            AddBox(vertexList, indexList, new Vector3(-1.12f, 0.35f,  1.15f), new Vector3(0.35f, 0.55f, 0.65f), Color.Black);
            AddBox(vertexList, indexList, new Vector3( 1.12f, 0.35f,  1.15f), new Vector3(0.35f, 0.55f, 0.65f), Color.Black);

            AddBox(vertexList, indexList, new Vector3(0f, 1.1f, -1.73f), new Vector3(1.5f, 0.18f, 0.08f), Color.White);
            AddBox(vertexList, indexList, new Vector3(0f, 0.9f,  1.73f), new Vector3(1.5f, 0.18f, 0.08f), Color.Red);

            vertices = vertexList.ToArray();
            indices = indexList.ToArray();
        }

        /// <summary>
        /// Adds a colored rectangular prism to the placeholder mesh buffers.
        /// </summary>
        private static void AddBox(List<VertexPositionColor> vertices, List<short> indices, Vector3 center, Vector3 size, Color color)
        {
            short start = (short)vertices.Count;
            Vector3 h = size * 0.5f;

            vertices.Add(new VertexPositionColor(center + new Vector3(-h.X, -h.Y, -h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3( h.X, -h.Y, -h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3( h.X,  h.Y, -h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3(-h.X,  h.Y, -h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3(-h.X, -h.Y,  h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3( h.X, -h.Y,  h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3( h.X,  h.Y,  h.Z), color));
            vertices.Add(new VertexPositionColor(center + new Vector3(-h.X,  h.Y,  h.Z), color));

            AddFace(indices, start, 0, 1, 2, 3);
            AddFace(indices, start, 5, 4, 7, 6);
            AddFace(indices, start, 4, 0, 3, 7);
            AddFace(indices, start, 1, 5, 6, 2);
            AddFace(indices, start, 3, 2, 6, 7);
            AddFace(indices, start, 4, 5, 1, 0);
        }

        /// <summary>
        /// Adds two triangles for one quad face of a placeholder box.
        /// </summary>
        private static void AddFace(List<short> indices, short start, short a, short b, short c, short d)
        {
            indices.Add((short)(start + a));
            indices.Add((short)(start + b));
            indices.Add((short)(start + c));
            indices.Add((short)(start + a));
            indices.Add((short)(start + c));
            indices.Add((short)(start + d));
        }
        #endregion
    }
}