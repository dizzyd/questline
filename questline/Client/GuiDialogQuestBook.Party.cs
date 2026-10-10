using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace questline.Client;

/// <summary>The Party tab: make a party, invite players, answer invites, leave, kick.</summary>
public partial class GuiDialogQuestBook
{
    void ComposeParty(GuiComposer composer)
    {
        var party = client.Party;
        // The full width, so the book keeps its size from tab to tab; the text keeps to a readable column.
        composer.AddInset(ElementBounds.Fixed(0, ContentY, Width, ContentHeight - 40), 3);
        var textBounds = ElementBounds.Fixed(8, ContentY + 8, System.Math.Min(Width - 16, 560), ContentHeight - 56);
        composer.AddRichtext(PartyComponents(), textBounds, "party");

        if (party is not { Enabled: true }) return;

        double buttonY = ContentY + ContentHeight - 30;
        if (!client.InParty)
        {
            composer.AddSmallButton(Lang.Get("questline:party-create"), () => { client.PartyAction("create"); return true; },
                ElementBounds.Fixed(0, buttonY, 160, 26), EnumButtonStyle.Normal, "party-create");
            return;
        }

        composer.AddSmallButton(Lang.Get("questline:party-leave"), () => { client.PartyAction("leave"); return true; },
            ElementBounds.Fixed(0, buttonY, 140, 26), EnumButtonStyle.Normal, "party-leave");

        if (party.Owner != capi.World.Player.PlayerUID) return;

        var members = party.Members.Select(m => m.Uid).ToHashSet();
        var candidates = capi.World.AllOnlinePlayers.Where(p => !members.Contains(p.PlayerUID)).ToList();
        if (candidates.Count == 0) return;

        inviteUid ??= candidates[0].PlayerUID;
        int index = System.Math.Max(0, candidates.FindIndex(p => p.PlayerUID == inviteUid));
        inviteUid = candidates[index].PlayerUID;
        composer
            .AddDropDown(candidates.Select(p => p.PlayerUID).ToArray(), candidates.Select(p => p.PlayerName).ToArray(), index,
                (uid, _) => inviteUid = uid, ElementBounds.Fixed(160, buttonY, 180, 26), "party-invitee")
            .AddSmallButton(Lang.Get("questline:party-invite"), () => { client.PartyAction("invite", inviteUid); return true; },
                ElementBounds.Fixed(350, buttonY, 100, 26), EnumButtonStyle.Normal, "party-invite");
    }

    string inviteUid;

    RichTextComponentBase[] PartyComponents()
    {
        var small = CairoFont.WhiteSmallText();
        var detail = CairoFont.WhiteDetailText();
        var party = client.Party;
        var comps = new List<RichTextComponentBase>();

        if (party == null) return [new RichTextComponent(capi, Lang.Get("questline:party-waiting"), small)];
        if (!party.Enabled) return [new RichTextComponent(capi, Lang.Get("questline:party-off"), small)];

        if (!client.InParty)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-none") + "\n\n", small));
        }
        else
        {
            comps.Add(new RichTextComponent(capi, party.Name + "\n", CairoFont.WhiteMediumText()));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-shares") + "\n\n", detail));
            bool owner = party.Owner == capi.World.Player.PlayerUID;
            foreach (var member in party.Members)
            {
                string line = member.Name + (member.Uid == party.Owner ? " " + Lang.Get("questline:party-owner") : "");
                comps.Add(new RichTextComponent(capi, (member.Online ? "● " : "○ ") + line, small));
                if (owner && member.Uid != party.Owner)
                {
                    string uid = member.Uid;
                    comps.Add(new LinkTextComponent(capi, "  " + Lang.Get("questline:party-kick"), small, _ => client.PartyAction("kick", uid)));
                }
                comps.Add(new RichTextComponent(capi, "\n", small));
            }
            comps.Add(new RichTextComponent(capi, "\n", small));
        }

        if (party.Invites.Count > 0)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-invites") + "\n", small.Clone().WithWeight(Cairo.FontWeight.Bold)));
            foreach (var invite in party.Invites)
            {
                string id = invite.PartyId;
                comps.Add(new RichTextComponent(capi, Lang.Get("questline:party-invite-from", invite.PartyName, invite.OwnerName), small));
                if (!client.InParty)
                {
                    comps.Add(new LinkTextComponent(capi, "  " + Lang.Get("questline:party-accept"), small, _ => client.PartyAction("accept", id)));
                }
                comps.Add(new LinkTextComponent(capi, "  " + Lang.Get("questline:party-decline"), small, _ => client.PartyAction("decline", id)));
                comps.Add(new RichTextComponent(capi, "\n", small));
            }
        }

        return comps.ToArray();
    }
}
