using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace questline.Model;

/// <summary>What a requirement is evaluated against: one player's view of the quest world.</summary>
public interface IRequirementContext
{
    bool IsQuestComplete(string questId);
    bool IsModLoaded(string modId);
    bool HasStage(string stage);
    bool IsChapterComplete(string chapterId);
}

/// <summary>
/// A quest's <c>requires</c>: a boolean tree. In JSON a bare string is a quest id, an array is
/// <c>all</c>, and an object's keys (<c>all</c>, <c>any</c>, <c>atLeast</c>, <c>quest</c>,
/// <c>mod</c>, <c>stage</c>, <c>chapterComplete</c>) must all hold.
/// </summary>
[JsonConverter(typeof(RequirementConverter))]
public abstract class Requirement
{
    public abstract bool Evaluate(IRequirementContext ctx);

    /// <summary>Every node in the tree, this one included.</summary>
    public virtual IEnumerable<Requirement> Walk()
    {
        yield return this;
    }

    public IEnumerable<string> QuestIds() => Walk().OfType<QuestRequirement>().Select(q => q.QuestId);

    /// <summary>Qualifies unqualified quest and chapter ids with <paramref name="domain"/>.</summary>
    public virtual void Qualify(string domain) { }

    internal static string QualifyId(string id, string domain) => id.Contains(':') ? id : domain + ":" + id;
}

public class QuestRequirement(string questId) : Requirement
{
    public string QuestId { get; private set; } = questId;
    public override bool Evaluate(IRequirementContext ctx) => ctx.IsQuestComplete(QuestId);
    public override void Qualify(string domain) => QuestId = QualifyId(QuestId, domain);
}

public class ModRequirement(string modId) : Requirement
{
    public string ModId { get; } = modId;
    public override bool Evaluate(IRequirementContext ctx) => ctx.IsModLoaded(ModId);
}

public class StageRequirement(string stage) : Requirement
{
    public string Stage { get; } = stage;
    public override bool Evaluate(IRequirementContext ctx) => ctx.HasStage(Stage);
}

public class ChapterCompleteRequirement(string chapterId) : Requirement
{
    public string ChapterId { get; private set; } = chapterId;
    public override bool Evaluate(IRequirementContext ctx) => ctx.IsChapterComplete(ChapterId);
    public override void Qualify(string domain) => ChapterId = QualifyId(ChapterId, domain);
}

public abstract class CompositeRequirement(List<Requirement> of) : Requirement
{
    public List<Requirement> Of { get; } = of;

    public override IEnumerable<Requirement> Walk() => Of.SelectMany(r => r.Walk()).Prepend(this);

    public override void Qualify(string domain)
    {
        foreach (var r in Of) r.Qualify(domain);
    }
}

public class AllRequirement(List<Requirement> of) : CompositeRequirement(of)
{
    public override bool Evaluate(IRequirementContext ctx) => Of.All(r => r.Evaluate(ctx));
}

public class AnyRequirement(List<Requirement> of) : CompositeRequirement(of)
{
    public override bool Evaluate(IRequirementContext ctx) => Of.Any(r => r.Evaluate(ctx));
}

public class AtLeastRequirement(int n, List<Requirement> of) : CompositeRequirement(of)
{
    public int N { get; } = n;
    public override bool Evaluate(IRequirementContext ctx) => Of.Count(r => r.Evaluate(ctx)) >= N;
}

public class RequirementConverter : JsonConverter<Requirement>
{
    public override Requirement ReadJson(JsonReader reader, Type objectType, Requirement existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        return token.Type == JTokenType.Null ? null : Parse(token);
    }

    public static Requirement Parse(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.String:
                return new QuestRequirement((string)token);
            case JTokenType.Array:
                return new AllRequirement(ParseList(token));
            case JTokenType.Object:
                var parts = ((JObject)token).Properties().Select(ParseKey).ToList();
                return parts.Count == 1 ? parts[0] : new AllRequirement(parts);
            default:
                throw new JsonSerializationException($"A requirement must be a quest id, a list or an object, not {token.Type}");
        }
    }

    static Requirement ParseKey(JProperty p) => p.Name switch
    {
        "all" => new AllRequirement(ParseList(p.Value)),
        "any" => new AnyRequirement(ParseList(p.Value)),
        "atLeast" => new AtLeastRequirement((int)p.Value["n"], ParseList(p.Value["of"])),
        "quest" => new QuestRequirement((string)p.Value),
        "mod" => new ModRequirement((string)p.Value),
        "stage" => new StageRequirement((string)p.Value),
        "chapterComplete" => new ChapterCompleteRequirement((string)p.Value),
        _ => throw new JsonSerializationException($"Unknown requirement '{p.Name}'"),
    };

    static List<Requirement> ParseList(JToken token)
    {
        if (token is not JArray arr) throw new JsonSerializationException($"Expected a list of requirements, got {token?.Type}");
        return arr.Select(Parse).ToList();
    }

    public override void WriteJson(JsonWriter writer, Requirement value, JsonSerializer serializer) => ToToken(value).WriteTo(writer);

    static JToken ToToken(Requirement r) => r switch
    {
        QuestRequirement q => new JValue(q.QuestId),
        ModRequirement m => new JObject { ["mod"] = m.ModId },
        StageRequirement s => new JObject { ["stage"] = s.Stage },
        ChapterCompleteRequirement c => new JObject { ["chapterComplete"] = c.ChapterId },
        AllRequirement a => new JObject { ["all"] = new JArray(a.Of.Select(ToToken)) },
        AnyRequirement a => new JObject { ["any"] = new JArray(a.Of.Select(ToToken)) },
        AtLeastRequirement a => new JObject { ["atLeast"] = new JObject { ["n"] = a.N, ["of"] = new JArray(a.Of.Select(ToToken)) } },
        _ => throw new JsonSerializationException($"Can't write {r.GetType().Name}"),
    };
}
