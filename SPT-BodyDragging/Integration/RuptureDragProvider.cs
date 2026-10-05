using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Integration
{
    internal enum CorpseRoute { Native, Managed, Blocked }
    internal enum ProviderResult { Ok, Pending, Closed, NotManaged, NotAuthority, Busy, InvalidArgument, InvalidLease, Unsupported, Failed, Stale }
    internal struct ProviderInfo { internal bool IsAuthority; internal uint DeathSequence; }
    internal struct ProviderBody
    {
        internal int Index;
        internal bool Eligible;
        internal Vector3 Position, CenterOfMass, Velocity, AngularVelocity;
    }
    internal sealed class ProviderView
    {
        internal int Stage, GripIndex;
        internal ulong CompletedStep;
        internal float SimulationDeltaTime;
        internal uint Topology, TranslationId;
        internal Vector3 GripPosition;
        internal ProviderBody[] Bodies;
    }
    internal struct ProviderAcceleration { internal int Index; internal Vector3 Value; }

    // No assembly reference to Rupture: solo/standalone loading never resolves it.
    internal static class RuptureDragProvider
    {
        internal const string PluginGuid = "com.rootdarkarchon.rupture";
        private static bool _present, _broken;
        private static MethodInfo _inspect, _begin, _read, _submit, _end;
        private static Shape _beginShape, _frameShape, _forceShape, _viewShape, _bodyShape, _infoShape;
        private static object _released;
        private sealed class Shape
        {
            internal readonly Type Type;
            private readonly Dictionary<string, FieldInfo> _fields = new();
            internal Shape(Assembly assembly, string name)
            {
                Type = assembly.GetType("Rupture.Integration." + name, true);
                foreach (FieldInfo field in Type.GetFields(BindingFlags.Public | BindingFlags.Instance)) _fields.Add(field.Name, field);
            }
            internal object New() => Activator.CreateInstance(Type);
            internal T Get<T>(object value, string name) => (T)_fields[name].GetValue(value);
            internal T GetOrDefault<T>(object value, string name, T fallback) => _fields.TryGetValue(name, out FieldInfo field) ? (T)field.GetValue(value) : fallback;
            internal void Set(object value, string name, object data) => _fields[name].SetValue(value, data);
        }
        internal static void Initialize()
        {
            _present = Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin);
            if (!_present) return;
            try
            {
                Assembly assembly = plugin.Instance.GetType().Assembly;
                Type api = assembly.GetType("Rupture.Integration.CorpseDragV1", true);
                if ((int)api.GetProperty("AbiVersion").GetValue(null) != 1) throw new NotSupportedException("requires corpse drag ABI V1");
                _inspect = api.GetMethod("Inspect"); _begin = api.GetMethod("Begin"); _read = api.GetMethod("Read");
                _submit = api.GetMethod("Submit"); _end = api.GetMethod("End");
                if (_inspect == null || _begin == null || _read == null || _submit == null || _end == null) throw new MissingMethodException("incomplete corpse drag ABI");
                _beginShape = new Shape(assembly, "DragBegin"); _frameShape = new Shape(assembly, "DragFrame");
                _forceShape = new Shape(assembly, "DragAcceleration"); _viewShape = new Shape(assembly, "DragView");
                _bodyShape = new Shape(assembly, "DragBodyView"); _infoShape = new Shape(assembly, "CorpseControlInfo");
                _released = Enum.ToObject(assembly.GetType("Rupture.Integration.DragEndReason", true), 0);
                BodyDragLog.Info("[Rupture] Corpse drag ABI V1 available");
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static void Fail(Exception exception)
        {
            if (!_broken) BodyDragLog.Warning("[Rupture] Managed dragging unavailable: " + exception.GetBaseException().Message);
            _broken = true;
        }
        private static ProviderResult Call(MethodInfo method, object[] args)
        {
            if (_broken || method == null) return ProviderResult.Unsupported;
            try { return (ProviderResult)Convert.ToInt32(method.Invoke(null, args)); }
            catch (Exception exception) { Fail(exception); return ProviderResult.Failed; }
        }
        internal static CorpseRoute Inspect(Corpse corpse, out ProviderInfo info)
        {
            info = default;
            if (!_present) return CorpseRoute.Native;
            object[] args = { corpse, null };
            ProviderResult result = Call(_inspect, args);
            if (result == ProviderResult.NotManaged) return CorpseRoute.Native;
            if (result != ProviderResult.Ok || args[1] == null) return CorpseRoute.Blocked;
            try
            {
                info.IsAuthority = _infoShape.Get<bool>(args[1], "IsAuthority");
                info.DeathSequence = _infoShape.Get<uint>(args[1], "DeathSequence");
                if ((Convert.ToInt32(_infoShape.Get<object>(args[1], "Capabilities")) & 7) != 7) return CorpseRoute.Blocked;
                return CorpseRoute.Managed;
            }
            catch (Exception exception) { Fail(exception); return CorpseRoute.Blocked; }
        }
        internal static ProviderResult Begin(Corpse corpse, float slack, float speed, out ulong lease)
        {
            lease = 0;
            if (_broken || _beginShape == null) return ProviderResult.Unsupported;
            object request = _beginShape.New();
            _beginShape.Set(request, "GripSlack", slack); _beginShape.Set(request, "MaxGripSpeed", speed);
            _beginShape.Set(request, "LeaseTimeout", 5f);
            object[] args = { corpse, request, 0UL };
            ProviderResult result = Call(_begin, args);
            lease = (ulong)args[2];
            return result;
        }
        internal static ProviderResult Read(ulong lease, out ProviderView view)
        {
            view = null;
            object[] args = { lease, null };
            ProviderResult result = Call(_read, args);
            if (args[1] == null) return result;
            try
            {
                object raw = args[1];
                Array bodies = _viewShape.Get<Array>(raw, "Bodies");
                view = new ProviderView
                {
                    Stage = Convert.ToInt32(_viewShape.Get<object>(raw, "Stage")),
                    CompletedStep = _viewShape.Get<ulong>(raw, "CompletedStep"),
                    SimulationDeltaTime = _viewShape.GetOrDefault(raw, "SimulationDeltaTime", 0f),
                    Topology = _viewShape.Get<uint>(raw, "TopologyRevision"), GripIndex = _viewShape.Get<int>(raw, "GripBodyIndex"),
                    GripPosition = _viewShape.Get<Vector3>(raw, "GripPosition"), TranslationId = _viewShape.Get<uint>(raw, "LastTranslationId"),
                    Bodies = new ProviderBody[bodies.Length]
                };
                for (int i = 0; i < bodies.Length; i++)
                {
                    object body = bodies.GetValue(i);
                    view.Bodies[i] = new ProviderBody
                    {
                        Index = _bodyShape.Get<int>(body, "BodyIndex"), Eligible = _bodyShape.Get<bool>(body, "Eligible"),
                        Position = _bodyShape.Get<Vector3>(body, "Position"), CenterOfMass = _bodyShape.Get<Vector3>(body, "WorldCenterOfMass"),
                        Velocity = _bodyShape.Get<Vector3>(body, "Velocity"), AngularVelocity = _bodyShape.Get<Vector3>(body, "AngularVelocity")
                    };
                }
            }
            catch (Exception exception) { Fail(exception); view = null; return ProviderResult.Failed; }
            return result;
        }
        internal static ProviderResult Submit(ulong lease, uint sequence, uint topology, Vector3 target,
            ProviderAcceleration[] acceleration, uint translationId, Vector3 translation)
        {
            if (_broken || _frameShape == null) return ProviderResult.Unsupported;
            object frame = _frameShape.New();
            _frameShape.Set(frame, "Sequence", sequence); _frameShape.Set(frame, "TopologyRevision", topology);
            _frameShape.Set(frame, "GripTarget", target); _frameShape.Set(frame, "TranslationId", translationId);
            _frameShape.Set(frame, "RigTranslation", translation);
            Array forces = Array.CreateInstance(_forceShape.Type, acceleration.Length);
            for (int i = 0; i < acceleration.Length; i++)
            {
                object force = _forceShape.New();
                _forceShape.Set(force, "BodyIndex", acceleration[i].Index); _forceShape.Set(force, "Acceleration", acceleration[i].Value);
                forces.SetValue(force, i);
            }
            _frameShape.Set(frame, "Accelerations", forces);
            return Call(_submit, new[] { (object)lease, frame });
        }
        internal static void End(ulong lease) { if (lease != 0) Call(_end, new[] { (object)lease, _released }); }
    }
}
