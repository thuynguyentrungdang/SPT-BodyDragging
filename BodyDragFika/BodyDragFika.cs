using System;
using System.Collections.Generic;
using BepInEx.Logging;
using BodyDragging;
using BodyDragging.Features;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragFika
{
    // Optional bridge: mirrors BodyDragging's corpse drag across a Fika raid. Deliberately NOT a
    // BepInEx plugin - ships beside BodyDragging.dll and is loaded by hand, by reflection, only
    // once the main plugin sees com.fika.core in the chainloader. BepInEx's Cecil scan finds no
    // plugin type here and never resolves Fika.Core, so a solo install never sees a load error.
    //
    // Native corpses retain the dragger-owned pose route. Rupture-managed corpses
    // use authenticated camera intent and a host lease instead; only Rupture
    // publishes their body motion. Headless uses the same managed controller.
    public static partial class BodyDragFikaBridge
    {
        private static ManualLogSource _log;
        private static bool _initialized;
        private static bool _registered;
        private static float _nextRetry;

        // host only: who currently owns each corpse. null value = the host itself (the host has
        // no NetPeer representing it)
        private static readonly Dictionary<string, NetPeer> Claims = new();
        // both roles: profiles currently known to be claimed by someone else, used to hide the
        // DRAG BODY action and to gate CorpseDragController.Begin before even asking the host
        private static readonly HashSet<string> RemotelyClaimedProfiles = new();

        public static void Initialize(ManualLogSource log)
        {
            if (_initialized)
                return;
            _log = log;

            FikaEventDispatcher.SubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(OnPeerDisconnected);

            BodyDragSync.DragStartRequested += OnLocalDragStartRequested;
            BodyDragSync.PoseSent += OnLocalPoseSent;
            BodyDragSync.DragStoppedLocally += OnLocalDragStopped;
            BodyDragSync.IsProfileAlreadyDragged = profileId => RemotelyClaimedProfiles.Contains(profileId);
            BodyDragSync.Tick = Tick;
            AttachManagedTransport();

            _initialized = true;
            _log?.LogInfo("[BodyDragFika] bridge attached, waiting for a raid");
        }

        public static void Shutdown()
        {
            if (!_initialized)
                return;

            FikaEventDispatcher.UnsubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
            FikaEventDispatcher.UnsubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
            FikaEventDispatcher.UnsubscribeEvent<PeerDisconnectedEvent>(OnPeerDisconnected);

            BodyDragSync.DragStartRequested -= OnLocalDragStartRequested;
            BodyDragSync.PoseSent -= OnLocalPoseSent;
            BodyDragSync.DragStoppedLocally -= OnLocalDragStopped;
            BodyDragSync.IsProfileAlreadyDragged = null;
            BodyDragSync.Tick = null;
            DetachManagedTransport();

            LeaveRaid();
            _initialized = false;
            _log?.LogInfo("[BodyDragFika] bridge detached");
        }

        private static void LeaveRaid()
        {
            CorpseDragController.StopActiveDrag();
            ManagedDragAuthority.ReleaseAll();
            ClearManagedClaims();
            BodyDragSync.LeaveRaid();
            RemoteCorpseDragFollower.ReleaseAll();
            Claims.Clear();
            RemotelyClaimedProfiles.Clear();
            _registered = false;
        }

        private static void OnGameCreated(FikaGameCreatedEvent e)
        {
            Enqueue(ApplyGameCreated);
        }

        private static void ApplyGameCreated()
        {
            BodyDragSync.Active = true;
            BodyDragSync.IsHost = FikaBackendUtils.IsServer;
            BodyDragSync.HeadlessHost = FikaBackendUtils.IsHeadlessGame || FikaBackendUtils.IsHeadless;
            _log?.LogInfo($"[BodyDragFika] raid started, this client is {(BodyDragSync.IsHost ? "HOST" : "CLIENT")}, headless host: {BodyDragSync.HeadlessHost}");
            RegisterPackets();
        }

        private static void OnGameEnded(FikaGameEndedEvent e)
        {
            Enqueue(() => { LeaveRaid(); _log?.LogInfo("[BodyDragFika] raid ended, sync off"); });
        }

        // a disconnected dragger's claim must free up, or their corpse can never be dragged
        // again; the follower side times its own drag out independently once poses stop arriving
        private static void OnPeerDisconnected(PeerDisconnectedEvent e)
        {
            NetPeer peer = e.Peer;
            Enqueue(() => ReleasePeer(peer));
        }

        private static void ReleasePeer(NetPeer peer)
        {
            if (!BodyDragSync.IsHost)
                return;
            List<string> freed = null;
            foreach (KeyValuePair<string, NetPeer> claim in Claims)
                if (ReferenceEquals(claim.Value, peer))
                    (freed ??= new List<string>()).Add(claim.Key);
            if (freed == null)
                return;
            foreach (string profileId in freed)
            {
                if (ManagedClaims.TryGetValue(profileId, out ManagedClaim managed))
                    ManagedDragAuthority.End(new ManagedDragEnd { Session = managed.Identity.Session,
                        ProfileId = managed.Identity.ProfileId, DeathSequence = managed.Identity.DeathSequence });
                else
                {
                    DragStopPacket packet = new DragStopPacket { ProfileId = profileId };
                    BodyDragSync.ApplyRemoteStop?.Invoke(profileId, packet.ToPose());
                    Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                }
                Claims.Remove(profileId);
                RemotelyClaimedProfiles.Remove(profileId);
                _log?.LogInfo($"[BodyDragFika] peer disconnected, freed claim on {profileId}");
            }
        }

        // the raid's network manager may not exist yet on the frame it's created; the main
        // plugin drives this retry from the Update it already runs
        private static void Tick()
        {
            DrainMainThread();
            if (!BodyDragSync.Active || _registered || Time.time < _nextRetry)
                return;
            _nextRetry = Time.time + 1f;
            RegisterPackets();
        }

        private static void RegisterPackets()
        {
            if (_registered)
                return;
            try
            {
                int epoch = _networkEpoch;
                if (BodyDragSync.IsHost)
                {
                    FikaServer server = Singleton<FikaServer>.Instance;
                    if (server == null)
                        return;

                    server.RegisterPacket<DragStartPacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        if (ManagedClaims.ContainsKey(packet.ProfileId) || !ManagedDragAuthority.CanUseNative(packet.ProfileId) ||
                            (Claims.TryGetValue(packet.ProfileId, out NetPeer holder) && holder != peer))
                        {
                            DragDenyPacket deny = new DragDenyPacket { ProfileId = packet.ProfileId };
                            server.SendDataToPeer(ref deny, DeliveryMethod.ReliableOrdered, peer);
                            return;
                        }
                        Claims[packet.ProfileId] = peer;
                        RemotelyClaimedProfiles.Add(packet.ProfileId);
                        DragStartPacket relay = new DragStartPacket { ProfileId = packet.ProfileId };
                        server.SendData(ref relay, DeliveryMethod.ReliableOrdered, peer);
                    }, epoch));
                    server.RegisterPacket<DragPosePacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        if (ManagedClaims.ContainsKey(packet.ProfileId) || !Claims.TryGetValue(packet.ProfileId, out NetPeer holder) || holder != peer ||
                            !ManagedDragAuthority.CanUseNative(packet.ProfileId))
                            return;
                        if (!(BodyDragSync.HeadlessHost && Plugin.HeadlessApplyFinalPoseOnly.Value))
                            BodyDragSync.ApplyRemotePose?.Invoke(packet.ProfileId, packet.ToPose());
                        server.SendData(ref packet, DeliveryMethod.Sequenced, peer);
                    }, epoch));
                    server.RegisterPacket<DragStopPacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        if (ManagedClaims.ContainsKey(packet.ProfileId) || !Claims.TryGetValue(packet.ProfileId, out NetPeer holder) || holder != peer) return;
                        Claims.Remove(packet.ProfileId);
                        RemotelyClaimedProfiles.Remove(packet.ProfileId);
                        BodyDragSync.ApplyRemoteStop?.Invoke(packet.ProfileId, packet.ToPose());
                        server.SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
                    }, epoch));
                    RegisterManagedServer(server, epoch);
                }
                else
                {
                    FikaClient client = Singleton<FikaClient>.Instance;
                    if (client == null)
                        return;

                    client.RegisterPacket<DragStartPacket>(packet => Enqueue(() =>
                    {
                        RemotelyClaimedProfiles.Add(packet.ProfileId);
                    }, epoch));
                    client.RegisterPacket<DragDenyPacket>(packet => Enqueue(() =>
                    {
                        BodyDragSync.ApplyDragDenied?.Invoke(packet.ProfileId);
                    }, epoch));
                    client.RegisterPacket<DragPosePacket>(packet => Enqueue(() =>
                    {
                        if (KnownManagedClaims.ContainsKey(packet.ProfileId)) return;
                        BodyDragSync.ApplyRemotePose?.Invoke(packet.ProfileId, packet.ToPose());
                    }, epoch));
                    client.RegisterPacket<DragStopPacket>(packet => Enqueue(() =>
                    {
                        if (KnownManagedClaims.ContainsKey(packet.ProfileId)) return;
                        RemotelyClaimedProfiles.Remove(packet.ProfileId);
                        BodyDragSync.ApplyRemoteStop?.Invoke(packet.ProfileId, packet.ToPose());
                    }, epoch));
                    RegisterManagedClient(client, epoch);
                }
                _registered = true;
                _log?.LogInfo($"[BodyDragFika] packets registered as {(BodyDragSync.IsHost ? "HOST" : "CLIENT")} - drags ARE synced");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BodyDragFika] could not register packets, drags run UNSYNCED: {ex}");
            }
        }

        private static void OnLocalDragStartRequested(string profileId)
        {
            if (!BodyDragSync.Active)
                return;
            DragStartPacket packet = new DragStartPacket { ProfileId = profileId };
            if (BodyDragSync.IsHost)
            {
                if (Claims.TryGetValue(profileId, out NetPeer holder) && holder != null)
                {
                    BodyDragSync.ApplyDragDenied?.Invoke(profileId);
                    return;
                }
                Claims[profileId] = null;
                RemotelyClaimedProfiles.Add(profileId);
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }

        private static void OnLocalPoseSent(BodyDragPose pose)
        {
            if (!BodyDragSync.Active)
                return;
            DragPosePacket packet = DragPosePacket.FromPose(pose);
            if (BodyDragSync.IsHost)
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.Sequenced);
            else
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.Sequenced);
        }

        private static void OnLocalDragStopped(string profileId, BodyDragPose pose)
        {
            if (!BodyDragSync.Active)
                return;
            DragStopPacket packet = DragStopPacket.FromPose(pose);
            if (BodyDragSync.IsHost)
            {
                Claims.Remove(profileId);
                RemotelyClaimedProfiles.Remove(profileId);
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    public struct DragStartPacket : INetSerializable
    {
        public string ProfileId;

        public void Serialize(NetDataWriter writer) => writer.Put(ProfileId);
        public void Deserialize(NetDataReader reader) => ProfileId = reader.GetString();
    }

    public struct DragDenyPacket : INetSerializable
    {
        public string ProfileId;

        public void Serialize(NetDataWriter writer) => writer.Put(ProfileId);
        public void Deserialize(NetDataReader reader) => ProfileId = reader.GetString();
    }

    // shared wire layout for a mid-drag pose and the final stop pose: profile id, a sequence
    // number so an out-of-order Sequenced delivery can be dropped by the receiver, and one
    // position+rotation pair per ragdoll rigidbody (variable count - the TraumaCore-derived
    // chest-grab drag carries every body, not EFT's fixed 12-bone CorpseSyncPacket layout)
    public struct DragPosePacket : INetSerializable
    {
        public string ProfileId;
        public uint Sequence;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;

        public static DragPosePacket FromPose(BodyDragPose pose) => new DragPosePacket
        {
            ProfileId = pose.ProfileId,
            Sequence = pose.Sequence,
            BonePositions = pose.BonePositions,
            BoneRotations = pose.BoneRotations
        };

        public BodyDragPose ToPose() => new BodyDragPose
        {
            ProfileId = ProfileId,
            Sequence = Sequence,
            BonePositions = BonePositions,
            BoneRotations = BoneRotations
        };

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId);
            writer.PutUnmanaged(Sequence);
            int count = BonePositions?.Length ?? 0;
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                writer.PutUnmanaged(BonePositions[i]);
                writer.PutUnmanaged(BoneRotations[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString();
            Sequence = reader.GetUnmanaged<uint>();
            int count = reader.GetInt();
            BonePositions = new Vector3[count];
            BoneRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                BonePositions[i] = reader.GetUnmanaged<Vector3>();
                BoneRotations[i] = reader.GetUnmanaged<Quaternion>();
            }
        }
    }

    public struct DragStopPacket : INetSerializable
    {
        public string ProfileId;
        public uint Sequence;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;

        public static DragStopPacket FromPose(BodyDragPose pose) => new DragStopPacket
        {
            ProfileId = pose.ProfileId,
            Sequence = pose.Sequence,
            BonePositions = pose.BonePositions,
            BoneRotations = pose.BoneRotations
        };

        public BodyDragPose ToPose() => new BodyDragPose
        {
            ProfileId = ProfileId,
            Sequence = Sequence,
            BonePositions = BonePositions,
            BoneRotations = BoneRotations
        };

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId);
            writer.PutUnmanaged(Sequence);
            int count = BonePositions?.Length ?? 0;
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                writer.PutUnmanaged(BonePositions[i]);
                writer.PutUnmanaged(BoneRotations[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString();
            Sequence = reader.GetUnmanaged<uint>();
            int count = reader.GetInt();
            BonePositions = new Vector3[count];
            BoneRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                BonePositions[i] = reader.GetUnmanaged<Vector3>();
                BoneRotations[i] = reader.GetUnmanaged<Quaternion>();
            }
        }
    }
}
