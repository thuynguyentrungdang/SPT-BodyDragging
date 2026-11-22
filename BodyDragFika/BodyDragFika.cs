using System;
using System.Collections.Generic;
using BepInEx.Logging;
using BodyDragging;
using BodyDragging.Features;
using Comfort.Common;
using EFT;
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
    // Native corpses: no pose stream. The dragger claims the corpse (DragStart names the dragger),
    // sends its hold parameters ~10 times a second (DragHold) and a stop; every other peer
    // simulates its own copy of the body from those plus the dragger's synced position. The peer
    // that let go then sends one DragRest (settled pose) so all copies end up the same.
    // Rupture-managed corpses use authenticated camera intent and a host lease instead; only
    // Rupture publishes their body motion. Headless uses the same managed controller.
    public static partial class BodyDragFikaBridge
    {
        // how long after a stop the dropping peer may still send the rest (its copy has to settle first)
        private const float RestWaitSeconds = 15f;

        private static ManualLogSource _log;
        private static bool _initialized;
        private static bool _registered;
        private static float _nextRetry;

        // host only: who currently owns each corpse. null value = the host itself (the host has
        // no NetPeer representing it)
        private static readonly Dictionary<string, NetPeer> Claims = new();
        // host only: who let go of each corpse and may now send its rest, until when
        private static readonly Dictionary<string, (NetPeer Peer, float Until)> PendingRest = new();
        private static readonly List<string> RestScratch = new();
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
            BodyDragSync.HoldSent += OnLocalHoldSent;
            BodyDragSync.DragStoppedLocally += OnLocalDragStopped;
            BodyDragSync.RestDeclared += OnLocalRestDeclared;
            BodyDragSync.HandPoseSent += OnLocalHandPose;
            BodyDragSync.IsProfileAlreadyDragged = profileId => RemotelyClaimedProfiles.Contains(profileId);
            BodyDragSync.Tick = Tick;
            AttachManagedTransport();
            InitializeDowned();

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
            BodyDragSync.HoldSent -= OnLocalHoldSent;
            BodyDragSync.DragStoppedLocally -= OnLocalDragStopped;
            BodyDragSync.RestDeclared -= OnLocalRestDeclared;
            BodyDragSync.HandPoseSent -= OnLocalHandPose;
            BodyDragSync.IsProfileAlreadyDragged = null;
            BodyDragSync.Tick = null;
            DetachManagedTransport();
            ShutdownDowned();

            LeaveRaid();
            _initialized = false;
            _log?.LogInfo("[BodyDragFika] bridge detached");
        }

        private static void LeaveRaid()
        {
            CorpseDragController.StopActiveDrag();
            ManagedDragAuthority.ReleaseAll();
            ClearManagedClaims();
            ClearDowned();
            DragHands.ClearRemote();
            BodyDragSync.LeaveRaid();
            RemoteCorpseDragSim.ReleaseAll();
            // every corpse still held for a Rupture handoff goes back to Rupture with the raid
            HandoffLeases.AdoptAll();
            CorpseRestDeclarer.Clear();
            Claims.Clear();
            PendingRest.Clear();
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
        // again; the other peers' simulations also time out on their own once holds stop arriving
        private static void OnPeerDisconnected(PeerDisconnectedEvent e)
        {
            NetPeer peer = e.Peer;
            Enqueue(() => ReleasePeer(peer));
        }

        private static void ReleasePeer(NetPeer peer)
        {
            if (!BodyDragSync.IsHost)
                return;
            RestScratch.Clear();
            foreach (KeyValuePair<string, (NetPeer Peer, float Until)> pending in PendingRest)
                if (ReferenceEquals(pending.Value.Peer, peer))
                    RestScratch.Add(pending.Key);
            foreach (string profileId in RestScratch)
                PendingRest.Remove(profileId);
            RestScratch.Clear();

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
                    BodyDragSync.ApplyRemoteStop?.Invoke(profileId);
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
            TickDowned();
            if (PendingRest.Count != 0)
                ExpirePendingRest();
            if (!BodyDragSync.Active || _registered || Time.time < _nextRetry)
                return;
            _nextRetry = Time.time + 1f;
            RegisterPackets();
        }

        private static void ExpirePendingRest()
        {
            float now = Time.unscaledTime;
            RestScratch.Clear();
            foreach (KeyValuePair<string, (NetPeer Peer, float Until)> pending in PendingRest)
                if (now > pending.Value.Until)
                    RestScratch.Add(pending.Key);
            foreach (string profileId in RestScratch)
                PendingRest.Remove(profileId);
            RestScratch.Clear();
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
                        if (string.IsNullOrEmpty(packet.ProfileId) || string.IsNullOrEmpty(packet.Dragger) ||
                            ManagedClaims.ContainsKey(packet.ProfileId) || !ManagedDragAuthority.CanUseNative(packet.ProfileId) ||
                            (Claims.TryGetValue(packet.ProfileId, out NetPeer holder) && holder != peer))
                        {
                            DragDenyPacket deny = new DragDenyPacket { ProfileId = packet.ProfileId };
                            server.SendDataToPeer(ref deny, DeliveryMethod.ReliableOrdered, peer);
                            return;
                        }
                        Claims[packet.ProfileId] = peer;
                        PendingRest.Remove(packet.ProfileId);
                        RemotelyClaimedProfiles.Add(packet.ProfileId);
                        BodyDragSync.ApplyRemoteStart?.Invoke(packet.ProfileId, packet.Dragger);
                        DragStartPacket relay = packet;
                        server.SendData(ref relay, DeliveryMethod.ReliableOrdered, peer);
                    }, epoch));
                    server.RegisterPacket<DragHoldPacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        if (ManagedClaims.ContainsKey(packet.ProfileId) || !Claims.TryGetValue(packet.ProfileId, out NetPeer holder) || holder != peer ||
                            !ManagedDragAuthority.CanUseNative(packet.ProfileId))
                            return;
                        BodyDragSync.ApplyRemoteHold?.Invoke(packet.ToHold());
                        server.SendData(ref packet, DeliveryMethod.Sequenced, peer);
                    }, epoch));
                    server.RegisterPacket<DragStopPacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        if (ManagedClaims.ContainsKey(packet.ProfileId) || !Claims.TryGetValue(packet.ProfileId, out NetPeer holder) || holder != peer) return;
                        Claims.Remove(packet.ProfileId);
                        RemotelyClaimedProfiles.Remove(packet.ProfileId);
                        PendingRest[packet.ProfileId] = (peer, Time.unscaledTime + RestWaitSeconds);
                        BodyDragSync.ApplyRemoteStop?.Invoke(packet.ProfileId);
                        server.SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
                    }, epoch));
                    server.RegisterPacket<DragRestPacket, NetPeer>((packet, peer) => Enqueue(() =>
                    {
                        // only the peer that just let go of this body may say where it lies
                        if (!PendingRest.TryGetValue(packet.ProfileId, out (NetPeer Peer, float Until) pending) ||
                            !ReferenceEquals(pending.Peer, peer) || Time.unscaledTime > pending.Until || Claims.ContainsKey(packet.ProfileId))
                            return;
                        PendingRest.Remove(packet.ProfileId);
                        BodyDragSync.ApplyRemoteRest?.Invoke(packet.ToRest());
                        server.SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
                    }, epoch));
                    RegisterManagedServer(server, epoch);
                    RegisterDownedServer(server, epoch);
                    RegisterHandsServer(server, epoch);
                }
                else
                {
                    FikaClient client = Singleton<FikaClient>.Instance;
                    if (client == null)
                        return;

                    client.RegisterPacket<DragStartPacket>(packet => Enqueue(() =>
                    {
                        RemotelyClaimedProfiles.Add(packet.ProfileId);
                        BodyDragSync.ApplyRemoteStart?.Invoke(packet.ProfileId, packet.Dragger);
                    }, epoch));
                    client.RegisterPacket<DragDenyPacket>(packet => Enqueue(() =>
                    {
                        BodyDragSync.ApplyDragDenied?.Invoke(packet.ProfileId);
                    }, epoch));
                    client.RegisterPacket<DragHoldPacket>(packet => Enqueue(() =>
                    {
                        if (KnownManagedClaims.ContainsKey(packet.ProfileId)) return;
                        BodyDragSync.ApplyRemoteHold?.Invoke(packet.ToHold());
                    }, epoch));
                    client.RegisterPacket<DragStopPacket>(packet => Enqueue(() =>
                    {
                        if (KnownManagedClaims.ContainsKey(packet.ProfileId)) return;
                        RemotelyClaimedProfiles.Remove(packet.ProfileId);
                        BodyDragSync.ApplyRemoteStop?.Invoke(packet.ProfileId);
                    }, epoch));
                    client.RegisterPacket<DragRestPacket>(packet => Enqueue(() =>
                    {
                        if (KnownManagedClaims.ContainsKey(packet.ProfileId)) return;
                        BodyDragSync.ApplyRemoteRest?.Invoke(packet.ToRest());
                    }, epoch));
                    RegisterManagedClient(client, epoch);
                    RegisterDownedClient(client, epoch);
                    RegisterHandsClient(client, epoch);
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
            Player me = GamePlayerOwner.MyPlayer;
            if (!BodyDragSync.Active || me == null)
                return;
            DragStartPacket packet = new DragStartPacket { ProfileId = profileId, Dragger = me.ProfileId };
            if (BodyDragSync.IsHost)
            {
                if (Claims.TryGetValue(profileId, out NetPeer holder) && holder != null)
                {
                    BodyDragSync.ApplyDragDenied?.Invoke(profileId);
                    return;
                }
                Claims[profileId] = null;
                PendingRest.Remove(profileId);
                RemotelyClaimedProfiles.Add(profileId);
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }

        private static void OnLocalHoldSent(BodyDragHold hold)
        {
            if (!BodyDragSync.Active)
                return;
            DragHoldPacket packet = DragHoldPacket.FromHold(hold);
            if (BodyDragSync.IsHost)
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.Sequenced);
            else
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.Sequenced);
        }

        private static void OnLocalDragStopped(string profileId)
        {
            if (!BodyDragSync.Active)
                return;
            DragStopPacket packet = new DragStopPacket { ProfileId = profileId };
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

        private static void OnLocalRestDeclared(BodyDragRest rest)
        {
            if (!BodyDragSync.Active)
                return;
            DragRestPacket packet = DragRestPacket.FromRest(rest);
            if (BodyDragSync.IsHost)
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            else
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public struct DragStartPacket : INetSerializable
    {
        public string ProfileId;
        // profile id of the player doing the drag: where the other peers' hand goes
        public string Dragger;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId ?? ""); writer.Put(Dragger ?? "");
        }
        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString(128); Dragger = reader.GetString(128);
        }
    }

    public struct DragDenyPacket : INetSerializable
    {
        public string ProfileId;

        public void Serialize(NetDataWriter writer) => writer.Put(ProfileId);
        public void Deserialize(NetDataReader reader) => ProfileId = reader.GetString();
    }

    // ~10 Hz, Sequenced: the dragger's hold in its own yaw frame. Doubles as the heartbeat.
    public struct DragHoldPacket : INetSerializable
    {
        public string ProfileId;
        public uint Sequence;
        public float Yaw;
        public float Distance;
        public float LocalX;
        public float LocalZ;
        public float HeightOffset;

        public static DragHoldPacket FromHold(BodyDragHold hold) => new DragHoldPacket
        {
            ProfileId = hold.ProfileId, Sequence = hold.Sequence, Yaw = hold.Yaw, Distance = hold.Distance,
            LocalX = hold.LocalX, LocalZ = hold.LocalZ, HeightOffset = hold.HeightOffset
        };

        public BodyDragHold ToHold() => new BodyDragHold
        {
            ProfileId = ProfileId, Sequence = Sequence, Yaw = Yaw, Distance = Distance,
            LocalX = LocalX, LocalZ = LocalZ, HeightOffset = HeightOffset
        };

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId ?? "");
            writer.PutUnmanaged(Sequence);
            writer.Put(Yaw); writer.Put(Distance); writer.Put(LocalX); writer.Put(LocalZ); writer.Put(HeightOffset);
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString(128);
            Sequence = reader.GetUnmanaged<uint>();
            Yaw = reader.GetFloat(); Distance = reader.GetFloat(); LocalX = reader.GetFloat(); LocalZ = reader.GetFloat(); HeightOffset = reader.GetFloat();
        }
    }

    public struct DragStopPacket : INetSerializable
    {
        public string ProfileId;

        public void Serialize(NetDataWriter writer) => writer.Put(ProfileId ?? "");
        public void Deserialize(NetDataReader reader) => ProfileId = reader.GetString(128);
    }

    // Where the body the sender just let go of came to rest: the corpse root and one position +
    // rotation per ragdoll rigidbody (variable count - not EFT's fixed 12-bone CorpseSyncPacket
    // layout). Reliable, once per drag.
    public struct DragRestPacket : INetSerializable
    {
        public const int MaximumBones = 64;
        public string ProfileId;
        public Vector3 Root;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;

        public static DragRestPacket FromRest(BodyDragRest rest) => new DragRestPacket
        {
            ProfileId = rest.ProfileId, Root = rest.Root, BonePositions = rest.BonePositions, BoneRotations = rest.BoneRotations
        };

        public BodyDragRest ToRest() => new BodyDragRest
        {
            ProfileId = ProfileId, Root = Root, BonePositions = BonePositions, BoneRotations = BoneRotations
        };

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId ?? "");
            writer.PutUnmanaged(Root);
            int count = Math.Min(BonePositions?.Length ?? 0, MaximumBones);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                writer.PutUnmanaged(BonePositions[i]);
                writer.PutUnmanaged(BoneRotations[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString(128);
            Root = reader.GetUnmanaged<Vector3>();
            int count = reader.GetInt();
            // a bad count from the wire reads as no pose (the receiver rejects a length that doesn't match the corpse)
            if (count < 0 || count > MaximumBones)
            {
                BonePositions = new Vector3[0];
                BoneRotations = new Quaternion[0];
                return;
            }
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
