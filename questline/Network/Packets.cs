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

/// <summary>The player's party and pending invites, sent whenever either changes.</summary>
[ProtoContract]
public class PartyPacket
{
    /// <summary>False when the server has parties turned off.</summary>
    [ProtoMember(1)] public bool Enabled;
    /// <summary>Null when the player isn't in a party.</summary>
    [ProtoMember(2)] public string Id;
    [ProtoMember(3)] public string Name;
    [ProtoMember(4)] public string Owner;
    [ProtoMember(5)] public List<PartyMember> Members = [];
    [ProtoMember(6)] public List<PartyInvite> Invites = [];
}

[ProtoContract]
public class PartyMember
{
    [ProtoMember(1)] public string Uid;
    [ProtoMember(2)] public string Name;
    [ProtoMember(3)] public bool Online;
}

[ProtoContract]
public class PartyInvite
{
    [ProtoMember(1)] public string PartyId;
    [ProtoMember(2)] public string PartyName;
    [ProtoMember(3)] public string OwnerName;
}

/// <summary>A party action from the book: create, invite, accept, decline, leave or kick.</summary>
[ProtoContract]
public class PartyRequest
{
    [ProtoMember(1)] public string Action;
    /// <summary>create: the name; invite and kick: the player's uid; accept and decline: the party id.</summary>
    [ProtoMember(2)] public string Arg;
}
