using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Every language's Questline text, as the game loaded it. Each one has every line English has,
    /// formats with the arguments the code passes, and keeps the English's handbook links, so a
    /// quest's guide opens whatever the player's language. tools/lang/check.py checks the files
    /// the same way without the game.
    /// </summary>
    public class TranslationTests
    {
        static readonly Regex Argument = new(@"\{p?(\d+)[:}]");
        static readonly Regex Href = new("href=\"([^\"]*)\"");

        [VsTest]
        public void EveryLanguageHasAllOfQuestlinesText()
        {
            var english = English().Keys.ToHashSet();
            var lacking = new List<string>();
            foreach (var (code, language) in Lang.AvailableLanguages)
            {
                var missing = english.Where(k => !language.HasTranslation(k, false)).ToList();
                if (missing.Count > 0) lacking.Add($"{code} lacks {missing.Count}: {string.Join(", ", missing.Take(5))}");
            }
            Assert.True(lacking.Count == 0, string.Join("\n", lacking));
        }

        [VsTest]
        public void EveryLineFormatsInEveryLanguage()
        {
            var failures = new List<string>();
            foreach (var (key, english) in English())
            {
                int passed = Argument.Matches(english).Select(m => int.Parse(m.Groups[1].Value) + 1).DefaultIfEmpty(0).Max();
                var links = Href.Matches(english).Select(m => m.Groups[1].Value).Order().ToList();
                foreach (string code in Lang.AvailableLanguages.Keys)
                {
                    try
                    {
                        string text = Lang.GetL(code, key, Enumerable.Range(1, passed).Select(n => (object)n).ToArray());
                        if (text.Contains("{p")) failures.Add($"{code} {key}: {text}");
                        if (!links.SequenceEqual(Href.Matches(text).Select(m => m.Groups[1].Value).Order())) failures.Add($"{code} {key}: links differ");
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{code} {key}: {e.Message}");
                    }
                }
            }
            Assert.True(failures.Count == 0, failures.Count + " lines fail:\n" + string.Join("\n", failures.Take(20)));
        }

        static Dictionary<string, string> English() =>
            Lang.AvailableLanguages["en"].GetAllEntries().Where(e => e.Key.StartsWith("questline:")).ToDictionary(e => e.Key, e => e.Value);
    }
}
