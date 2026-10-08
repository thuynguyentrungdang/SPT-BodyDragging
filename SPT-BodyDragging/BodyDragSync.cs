using System;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging
{
    // Seam between the main plugin and the optional BodyDragFika bridge DLL. No Fika type is
    // named here - the bridge is loaded by reflection only when Fika.Core is present in the
    // chainloader (see Plugin.TryLoadFikaBridge), so solo play never touches this beyond the
    // Active check staying false.
    public static class BodyDragSync
    {
        public static bool Active;
        public static bool IsHost;
        public static bool HeadlessHost;

        // the bridge's packet handlers can only attach once Fika's network manager exists;
        // the main plugin already runs an Update loop, so it drives the bridge's retry from there
        public static Action Tick;

        // set by the Fika bridge while the local player drags a downed teammate, so the same
        // movement slowdown as the corpse drag applies
        internal static bool ExternalDragActive;

        public static Func<ManagedDragStart, bool> ManagedStartRequested = ManagedDragAuthority.Begin;
        public static Action<ManagedDragInput> ManagedInputSent = input => ManagedDragAuthority.Input(input);
        public static Action<ManagedDragEnd> ManagedEndRequested = ManagedDragAuthority.End;
        public static Action<ManagedDragStatus> ManagedStatusSent;
        public static Action<ManagedDragStatus> ApplyManagedStatus;

        // outgoing, main plugin -> bridge
        public static Action<string> DragStartRequested;
        public static Action<BodyDragPose> PoseSent;
        public static Action<string, BodyDragPose> DragStoppedLocally;

        // incoming, bridge -> main plugin
        public static Action<string> ApplyDragDenied;
        public static Action<string, BodyDragPose> ApplyRemotePose;
        public static Action<string, BodyDragPose> ApplyRemoteStop;

        // bridge asks before granting a claim locally observed as free
        public static Func<string, bool> IsProfileAlreadyDragged;

        public static void LeaveRaid()
        {
            Active = false;
            IsHost = false;
            HeadlessHost = false;
        }
    }

    public struct BodyDragPose
    {
        public string ProfileId;
        public uint Sequence;
        public Vector3 Pelvis;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;
    }
}
