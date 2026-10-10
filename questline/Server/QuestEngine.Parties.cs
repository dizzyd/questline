using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using questline.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace questline.Server;

/// <summary>
/// Parties: players who pool progress on party quests. A player who joins brings their own
/// progress on party quests into the party's; one who leaves takes a copy of the party's
/// progress with them, so nobody loses a quest by changing parties.
/// </summary>
public partial class QuestEngine
{
    Dictionary<string, Party> parties = [];
    readonly Dictionary<string, Party> partyOf = [];

    public IReadOnlyCollection<Party> Parties => parties.Values;

    public Party PartyOf(string playerUid) => playerUid != null && partyOf.TryGetValue(playerUid, out var party) ? party : null;

    void IndexParties()
    {
        partyOf.Clear();
        foreach (var party in parties.Values)
        {
            foreach (string uid in party.Members) partyOf[uid] = party;
        }
    }

    void OnPartyRequest(IServerPlayer player, PartyRequest req)
    {
        string uid = player.PlayerUID;
        string error = req.Action switch
        {
            "create" => CreateParty(uid, req.Arg).Error,
            "invite" => Invite(uid, req.Arg),
            "accept" => Accept(uid, req.Arg),
            "decline" => Decline(uid, req.Arg),
            "leave" => Leave(uid),
            "kick" => Kick(uid, req.Arg),
            _ => $"unknown party action {req.Action}",
        };
        if (error != null) player.SendIngameError("questline-party", error);
    }

    /// <summary>Starts a party with the player as owner and only member.</summary>
    public (Party Party, string Error) CreateParty(string ownerUid, string name)
    {
        if (!Config.Parties) return (null, "Parties are turned off on this server.");
        if (PartyOf(ownerUid) != null) return (null, "Leave your party first.");

        var party = new Party
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? $"{NameOf(ownerUid)}'s party" : name.Trim(),
            Owner = ownerUid,
        };
        parties[party.Id] = party;
        Join(party, ownerUid);
        return (party, null);
    }

    public string Invite(string ownerUid, string targetUid)
    {
        var party = PartyOf(ownerUid);
        if (party == null) return "You're not in a party.";
        if (party.Owner != ownerUid) return "Only the party's owner can invite.";
        if (string.IsNullOrEmpty(targetUid)) return "Pick a player to invite.";
        if (party.Members.Contains(targetUid)) return "They're already in the party.";

        party.Invited.Add(targetUid);
        SendParty(targetUid);
        return null;
    }

    public string Accept(string playerUid, string partyId)
    {
        if (!Config.Parties) return "Parties are turned off on this server.";
        if (partyId == null || !parties.TryGetValue(partyId, out var party) || !party.Invited.Contains(playerUid)) return "That invite has gone.";
        if (PartyOf(playerUid) != null) return "Leave your party first.";

        Join(party, playerUid);
        return null;
    }

    public string Decline(string playerUid, string partyId)
    {
        if (partyId != null && parties.TryGetValue(partyId, out var party)) party.Invited.Remove(playerUid);
        SendParty(playerUid);
        return null;
    }

    public string Leave(string playerUid)
    {
        var party = PartyOf(playerUid);
        if (party == null) return "You're not in a party.";

        TakeCopy(party, playerUid);
        party.Members.Remove(playerUid);
        partyOf.Remove(playerUid);
        if (party.Members.Count == 0) parties.Remove(party.Id);
        else if (party.Owner == playerUid) party.Owner = party.Members[0];

        Changed(playerUid);
        foreach (string uid in party.Members) Changed(uid);
        FlushSync();
        return null;
    }

    public string Kick(string ownerUid, string targetUid)
    {
        var party = PartyOf(ownerUid);
        if (party == null || party.Owner != ownerUid) return "Only the party's owner can remove members.";
        if (targetUid == ownerUid || !party.Members.Contains(targetUid)) return "They're not in the party.";
        return Leave(targetUid);
    }

    void Join(Party party, string playerUid)
    {
        party.Invited.Remove(playerUid);
        BringIn(party, playerUid);
        party.Members.Add(playerUid);
        partyOf[playerUid] = party;

        foreach (string uid in party.Members) Changed(uid);
        FlushSync();
    }

    /// <summary>Merges the player's own progress on party quests into the party's: the further of the two counts.</summary>
    void BringIn(Party party, string playerUid)
    {
        foreach (var (questId, mine) in ProgressOf(playerUid).Quests)
        {
            if (Registry.Get(questId) is not { Mode: QuestMode.Party } quest) continue;
            Merge(party.Progress.GetOrCreate(quest), mine);
        }
    }

    /// <summary>Gives a leaving player a copy of the party's progress on party quests, and its stages.</summary>
    void TakeCopy(Party party, string playerUid)
    {
        var own = ProgressOf(playerUid);
        foreach (var (questId, theirs) in party.Progress.Quests)
        {
            if (Registry.Get(questId) is not { Mode: QuestMode.Party } quest) continue;
            Merge(own.GetOrCreate(quest), theirs);
        }
        own.Stages.UnionWith(party.Progress.Stages);
    }

    static void Merge(QuestProgress into, QuestProgress from)
    {
        for (int i = 0; i < Math.Min(into.Tasks.Length, from.Tasks.Length); i++) into.Tasks[i] = Math.Max(into.Tasks[i], from.Tasks[i]);
        if (from.Complete && !into.Complete)
        {
            into.Complete = true;
            into.CompletedDay = from.CompletedDay;
        }
        into.Seeded |= from.Seeded;
        into.Consumed |= from.Consumed;
    }

    /// <summary>A player's party or quests changed: resync their quests and party view.</summary>
    void Changed(string playerUid)
    {
        inventoryDirty.Add(playerUid);
        syncDirty.Add(playerUid);
        SendParty(playerUid);
    }

    string NameOf(string uid) =>
        api.World.PlayerByUid(uid)?.PlayerName ?? api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? uid;

    void SendParty(string playerUid)
    {
        // Connected as well as playing: when PlayerNowPlaying fires the state hasn't changed yet.
        if (api.World.PlayerByUid(playerUid) is not IServerPlayer { ConnectionState: EnumClientState.Playing or EnumClientState.Connected } player) return;

        var party = PartyOf(playerUid);
        var packet = new PartyPacket
        {
            Enabled = Config.Parties,
            Id = party?.Id,
            Name = party?.Name,
            Owner = party?.Owner,
            Members = party?.Members.Select(uid => new PartyMember
            {
                Uid = uid,
                Name = NameOf(uid),
                Online = api.World.PlayerByUid(uid) is IServerPlayer { ConnectionState: EnumClientState.Playing },
            }).ToList() ?? [],
            Invites = parties.Values.Where(p => p.Invited.Contains(playerUid)).Select(p => new PartyInvite
            {
                PartyId = p.Id,
                PartyName = p.Name,
                OwnerName = NameOf(p.Owner),
            }).ToList(),
        };
        channel.SendPacket(packet, player);
    }
}
