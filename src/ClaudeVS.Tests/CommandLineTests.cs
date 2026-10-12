using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace ClaudeVS.Tests
{
    public class CommandLineTests
    {
        [Theory]
        [InlineData("", "\"\"")]
        [InlineData("abc", "abc")]
        [InlineData("a b", "\"a b\"")]
        [InlineData("a\tb", "\"a\tb\"")]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData("a\\b", "a\\b")]                    // backslash seul : rien à échapper
        [InlineData("C:\\dir\\", "C:\\dir\\")]          // pas de guillemets, donc backslashes finaux intacts
        [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]        // 1 backslash avant guillemet -> 2 + 1
        [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]  // 2 backslashes avant guillemet -> 4 + 1
        [InlineData("a b\\", "\"a b\\\\\"")]            // backslash final doublé dans des guillemets
        [InlineData("a b\\\\", "\"a b\\\\\\\\\"")]
        [InlineData("\"", "\"\\\"\"")]
        public void Quote_cas_attendus(string arg, string expected) => Assert.Equal(expected, CommandLine.Quote(arg));

        [Fact]
        public void Quote_null_equivaut_a_vide() => Assert.Equal("\"\"", CommandLine.Quote(null));

        [Fact]
        public void Join_separe_par_des_espaces_et_echappe_chaque_argument()
            => Assert.Equal("a \"b c\" \"\" \"d\\\"e\"", CommandLine.Join(new[] { "a", "b c", "", "d\"e" }));

        [Fact]
        public void Join_liste_vide() => Assert.Equal("", CommandLine.Join(new string[0]));

        [Theory]
        [InlineData("")]
        [InlineData("simple")]
        [InlineData("avec espace")]
        [InlineData("tab\tulation")]
        [InlineData("guillemet\"au milieu")]
        [InlineData("backslash\\avant\\\"guillemet")]
        [InlineData("fin\\")]
        [InlineData("fin avec espace\\\\")]
        [InlineData("\\\\\\\"\\\\")]
        [InlineData("{\"a\": \"b c\"}")]
        public void Aller_retour_avec_les_regles_de_CommandLineToArgvW(string arg)
        {
            var parsed = ParseLikeCommandLineToArgvW(CommandLine.Join(new[] { "x", arg, "y" }));
            Assert.Equal(new[] { "x", arg, "y" }, parsed);
        }

        // Analyseur des règles documentées de CommandLineToArgvW (2n backslashes + " -> n backslashes et bascule ;
        // 2n+1 -> n backslashes et guillemet littéral ; sinon backslashes littéraux).
        private static List<string> ParseLikeCommandLineToArgvW(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            var started = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '\\')
                {
                    var n = 0;
                    while (i < line.Length && line[i] == '\\') { n++; i++; }
                    if (i < line.Length && line[i] == '"')
                    {
                        sb.Append('\\', n / 2);
                        if (n % 2 == 1) sb.Append('"'); else inQuotes = !inQuotes;
                    }
                    else { sb.Append('\\', n); i--; }
                    started = true;
                }
                else if (c == '"') { inQuotes = !inQuotes; started = true; }
                else if ((c == ' ' || c == '\t') && !inQuotes)
                {
                    if (started) { result.Add(sb.ToString()); sb.Clear(); started = false; }
                }
                else { sb.Append(c); started = true; }
            }
            if (started) result.Add(sb.ToString());
            return result;
        }
    }
}
