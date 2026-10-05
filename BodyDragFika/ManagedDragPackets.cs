using System;
using BodyDragging.Integration;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace BodyDragFika
{
    internal static class ManagedDragWire
    {
        internal static void Write(NetDataWriter writer, string session, string profile, uint death)
        {
            writer.Put((byte)Version); writer.Put(session); writer.Put(profile); writer.Put(death);
        }
        internal const int Version = 2;
        internal static void Read(NetDataReader reader, out string session, out string profile, out uint death)
        {
            if (reader.GetByte() != Version) throw new FormatException("unsupported managed drag protocol");
            session = reader.GetString(32); profile = reader.GetString(128); death = reader.GetUInt();
            if (!ManagedIntentBuffer.ValidSession(session) || string.IsNullOrEmpty(profile)) throw new FormatException("invalid managed drag identity");
        }
    }
    public struct ManagedDragStartPacket : INetSerializable
    {
        public ManagedDragStart Value;
        public void Serialize(NetDataWriter writer)
        {
            ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
            writer.Put(Value.DraggerProfileId ?? "");
        }
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
            Value.DraggerProfileId = reader.GetString(128);
        }
    }
    public struct ManagedDragInputPacket : INetSerializable
    {
        public ManagedDragInput Value;
        public void Serialize(NetDataWriter writer)
        {
            ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
            writer.Put(Value.Sequence); writer.Put(Value.HasTarget); writer.PutUnmanaged(Value.Target);
            writer.Put(Value.Distance); writer.Put(Value.LocalX); writer.Put(Value.LocalZ); writer.Put(Value.HeightOffset); writer.Put(Value.Yaw);
        }
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
            Value.Sequence = reader.GetUInt(); Value.HasTarget = reader.GetBool(); Value.Target = reader.GetUnmanaged<Vector3>();
            Value.Distance = reader.GetFloat(); Value.LocalX = reader.GetFloat(); Value.LocalZ = reader.GetFloat();
            Value.HeightOffset = reader.GetFloat(); Value.Yaw = reader.GetFloat();
            if (Value.Sequence == 0 || !ManagedIntentBuffer.Finite(Value.Target) || !ManagedIntentBuffer.FiniteHold(Value)) throw new FormatException("invalid managed drag target");
        }
    }
    public struct ManagedDragEndPacket : INetSerializable
    {
        public ManagedDragEnd Value;
        public void Serialize(NetDataWriter writer) => ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
        public void Deserialize(NetDataReader reader) => ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
    }
    public struct ManagedDragStatusPacket : INetSerializable
    {
        public ManagedDragStatus Value;
        public void Serialize(NetDataWriter writer)
        {
            ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
            writer.Put(Value.Sequence); writer.Put((byte)Value.Stage); writer.PutUnmanaged(Value.GripPoint);
        }
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
            Value.Sequence = reader.GetUInt(); Value.Stage = (ManagedDragStage)reader.GetByte(); Value.GripPoint = reader.GetUnmanaged<Vector3>();
            if (Value.Sequence == 0 || Value.Stage > ManagedDragStage.Failed || !ManagedIntentBuffer.Finite(Value.GripPoint)) throw new FormatException("invalid managed drag status");
        }
    }
}
