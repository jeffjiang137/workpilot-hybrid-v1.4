using System.Globalization;
using System.Text;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed record SkillCandidate(
    string SkillId, string VersionId, string Version, string Name, string Description,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, int SortOrder, bool Pinned,
    IReadOnlyList<string> RequiredCapabilities);

public static class SkillSelector
{
    public static IReadOnlyList<SkillActivationEvidence> Select(string message,
        IReadOnlyList<SkillCandidate> candidates, IReadOnlySet<string> availableCapabilities)
    {
        var normalized = Normalize(message.Length > 2000 ? message[..2000] : message);
        var tokens = Tokenize(normalized); var selected = new List<SkillActivationEvidence>();
        foreach (var candidate in candidates)
        {
            var missing = candidate.RequiredCapabilities.Where(x => !availableCapabilities.Contains(x)).ToList();
            if (missing.Count > 0)
            {
                selected.Add(new(candidate.SkillId, candidate.Version, candidate.Pinned, 0, [],
                    "缺少能力：" + string.Join(", ", missing)));
                continue;
            }
            if (candidate.Pinned)
            {
                selected.Add(new(candidate.SkillId, candidate.Version, true, double.MaxValue, ["专家固定"]));
                continue;
            }
            var evidence = Score(candidate, normalized, tokens);
            selected.Add(evidence.Score >= 3 ? evidence : evidence with { ExclusionReason = "匹配分数低于 3.0" });
        }
        var pinned = selected.Where(x => x.Pinned && x.ExclusionReason is null)
            .OrderBy(x => candidates.First(c => c.SkillId == x.SkillId).SortOrder).Take(20).ToList();
        var automatic = selected.Where(x => !x.Pinned && x.ExclusionReason is null)
            .OrderByDescending(x => Math.Round(x.Score, 6))
            .ThenBy(x => candidates.First(c => c.SkillId == x.SkillId).SortOrder)
            .ThenBy(x => x.SkillId, StringComparer.Ordinal).Take(Math.Max(0, 20 - pinned.Count)).Take(5);
        return [.. pinned, .. automatic];
    }

    private static SkillActivationEvidence Score(SkillCandidate candidate, string message,
        IReadOnlySet<string> messageTokens)
    {
        var matches = new List<string>(); double score = 0;
        foreach (var alias in candidate.Aliases.Take(20))
        {
            var normalizedAlias = Normalize(alias); if (normalizedAlias.Length == 0) continue;
            if (message.Contains(normalizedAlias, StringComparison.Ordinal))
            {
                score += 6; matches.Add("别名：" + alias); break;
            }
            var aliasHits = Tokenize(normalizedAlias).Count(messageTokens.Contains);
            if (aliasHits > 0) { score += Math.Min(3, aliasHits); matches.Add("别名词：" + alias); }
        }
        var tags = candidate.Tags.Select(Normalize).Where(x => x.Length > 0).Distinct().ToList();
        var tagHits = tags.Where(messageTokens.Contains).ToList();
        if (tagHits.Count > 0) { score += 3d * tagHits.Count / Math.Max(1, tags.Count); matches.Add("标签：" + string.Join("/", tagHits)); }
        var nameTokens = Tokenize(Normalize(candidate.Name)); var nameHits = nameTokens.Count(messageTokens.Contains);
        if (nameHits > 0) { score += 2d * nameHits / Math.Max(1, nameTokens.Count); matches.Add("名称"); }
        var descriptionTokens = Tokenize(Normalize(candidate.Description));
        var descriptionHits = descriptionTokens.Count(messageTokens.Contains);
        if (descriptionHits > 0) { score += Math.Min(1d, descriptionHits / (double)Math.Max(1, descriptionTokens.Count)); matches.Add("描述"); }
        return new(candidate.SkillId, candidate.Version, false, Math.Round(score, 6), matches);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal); var latin = new StringBuilder();
        void Flush() { if (latin.Length > 0) { result.Add(latin.ToString()); latin.Clear(); } }
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character >= 0x4E00 && character <= 0x9FFF)
            {
                Flush(); result.Add(character.ToString());
                if (index + 1 < value.Length && value[index + 1] >= 0x4E00 && value[index + 1] <= 0x9FFF)
                    result.Add(value.Substring(index, 2));
            }
            else if (char.IsLetterOrDigit(character)) latin.Append(character);
            else Flush();
        }
        Flush(); return result;
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC)
        .ToLower(CultureInfo.InvariantCulture).Trim();
}
