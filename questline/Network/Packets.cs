using System.Collections.Generic;
using ProtoBuf;
using questline.Model;

namespace questline.Network;

/// <summary>The whole <see cref="QuestRegistry"/>, sent once when a player joins and again on reload.</summary>
[ProtoContract]
public class DefinitionsPacket
{
    [ProtoMember(1)] public string RegistryJson;
}

/// <summary>Quest states for one player. The first after definitions is <see cref="Full"/>; later ones carry only changes.</summary>
[ProtoContract]
public class StatePacket
{
    [ProtoMember(1)] public bool Full;
    [ProtoMember(2)] public List<QuestStateEntry> Entries = [];
}

[ProtoContract]
public class QuestStateEntry
{
    [ProtoMember(1)] public string QuestId;
    [ProtoMember(2)] public QuestState State;
    [ProtoMember(3)] public int[] Tasks;
}

[ProtoContract]
public class ClaimRequest
{
    [ProtoMember(1)] public string QuestId;
    /// <summary>For each choice reward, in reward order, the option picked.</summary>
    [ProtoMember(2)] public int[] Choices;
}

[ProtoContract]
public class CheckboxRequest
{
    [ProtoMember(1)] public string QuestId;
    [ProtoMember(2)] public int TaskIndex;
}
