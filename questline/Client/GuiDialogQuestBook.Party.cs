using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// The Party tab: the party and its members on the left, invites on the right, each panel with
/// its own buttons in the row beneath it.
/// </summary>
public partial class GuiDialogQuestBook
{
    string inviteUid;

    double PartyLeftWidth => Math.Round((Width - Gap) * 0.55);
    double PartyRightX => PartyLeftWidth + Gap;
    double PartyRightWidth => Width - PartyRightX;

    void ComposeParty(GuiComposer composer)
    {
        var party = client.Party;
        AddTextPanel(composer, "party", 0, ContentY, PartyLeftWidth, PanelHeight, PartyComponents());
        AddTextPanel(composer, "invites", PartyRightX, ContentY, PartyRightWidth, PanelHeight, InviteComponents());

        if (party is not { Enabled: true }) return;

        if (!client.InParty)
        {
            AddRowButton(composer, Lang.Get("questline:party-create"), 0, false, () => { client.PartyAction("create"); return true; }, "party-create", primary: true);
            return;
        }

        AddRowButton(composer, Lang.Get("questline:party-leave"), 0, false, () => { client.PartyAction("leave"); return true; }, "party-leave");

        if (party.Owner != capi.World.Player.PlayerUID) return;

        var members = party.Members.Select(m => m.Uid).ToHashSet();
        var candidates = capi.World.AllOnlinePlayers.Where(p => !members.Contains(p.PlayerUID)).ToList();
        if (candidates.Count == 0) return;

        int index = Math.Max(0, candidates.FindIndex(p => p.PlayerUID == inviteUid));
        inviteUid = candidates[index].PlayerUID;
        double button = AddRowButton(composer, Lang.Get("questline:party-invite"), Width, true, () => { client.PartyAction("invite", inviteUid); return true; }, "party-invite", primary: true);
        composer.AddDropDown(candidates.Select(p => p.PlayerUID).ToArray(), candidates.Select(p => p.PlayerName).ToArray(), index,
            (uid, _) => inviteUid = uid, ElementBounds.Fixed(PartyRightX, ButtonY, PartyRightWidth - button - Gap / 2, ButtonHeight), "party-invitee");
    }

    RichTextComponentBase[] PartyComponents()
    {
        var party = client.Party;
        if (party == null) return [new RichTextComponent(capi, Lang.Get("questline:party-waiting"), Body().WithColor(Muted))];
        if (!party.Enabled) return [new RichTextComponent(capi, Lang.Get("questline:party-off"), Body().WithColor(Muted))];

        if (!client.InParty)
        {
            return
            [
                new RichTextComponent(capi, Lang.Get("questline:party-none-title") + "\n", Title()),
                new RichTextComponent(capi, Lang.Get("questline:party-none"), Body().WithColor(Muted)),
            ];
        }

        bool owner = party.Owner == capi.World.Player.PlayerUID;
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, party.Name + "\n", Title()),
            new RichTextComponent(capi, Lang.Get("questline:party-shares") + "\n", MutedSmall()),
            new RuleTextComponent(capi),
            new RichTextComponent(capi, Lang.Get("questline:party-members", party.Members.Count).ToUpperInvariant() + "\n", Heading()),
        };
        foreach (var member in party.Members)
        {
            comps.Add(new RichTextComponent(capi, "● ", Body().WithColor(member.Online ? Good : Muted)) { VerticalAlign = EnumVerticalAlign.Middle });
            comps.Add(new RichTextComponent(capi, member.Name, Body()) { VerticalAlign = EnumVerticalAlign.Middle });
            if (member.Uid == party.Owner)
            {
                comps.Add(new RichTextComponent(capi, "   " + Lang.Get("questline:party-owner"), MutedSmall()) { VerticalAlign = EnumVerticalAlign.Middle });
            }
            else if (owner)
            {
                string uid = member.Uid;
                comps.Add(new LinkTextComponent(capi, "   " + Lang.Get("questline:party-kick"), Link(), _ => client.PartyAction("kick", uid)) { VerticalAlign = EnumVerticalAlign.Middle });
            }
            comps.Add(new ClearFloatTextComponent(capi, 6));
        }
        return comps.ToArray();
    }

    RichTextComponentBase[] InviteComponents()
    {
        var party = client.Party;
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, Lang.Get("questline:party-invites").ToUpperInvariant() + "\n", Heading()),
        };
        if (party is not { Enabled: true }) return comps.ToArray();

        if (party.Invites.Count == 0)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-no-invites") + "\n", MutedSmall()));
        }
        foreach (var invite in party.Invites)
        {
            string id = invite.PartyId;
            comps.Add(new RichTextComponent(capi, invite.PartyName + "\n", Body().WithWeight(Cairo.FontWeight.Bold)));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-invite-from", invite.OwnerName) + "\n", MutedSmall()));
            if (!client.InParty) comps.Add(new LinkTextComponent(capi, Lang.Get("questline:party-accept") + "     ", Link(), _ => client.PartyAction("accept", id)));
            comps.Add(new LinkTextComponent(capi, Lang.Get("questline:party-decline") + "\n", Link(), _ => client.PartyAction("decline", id)));
            comps.Add(new ClearFloatTextComponent(capi, 10));
        }

        if (client.InParty && party.Owner == capi.World.Player.PlayerUID)
        {
            comps.Add(new RuleTextComponent(capi));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-invite-heading").ToUpperInvariant() + "\n", Heading()));
            comps.Add(new RichTextComponent(capi, Lang.Get(capi.World.AllOnlinePlayers.Length > party.Members.Count ? "questline:party-invite-hint" : "questline:party-invite-nobody"), MutedSmall()));
        }
        return comps.ToArray();
    }
}
