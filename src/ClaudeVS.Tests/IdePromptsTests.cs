using System;
using ClaudeVS.Chat;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeVS.Tests
{
    public class IdePromptsTests
    {
        private static JObject Sel(string text, bool empty, int start, int end, string path = "C:\\src\\Foo.cs")
            => new JObject
            {
                ["filePath"] = path,
                ["text"] = text,
                ["selection"] = new JObject { ["isEmpty"] = empty, ["start"] = new JObject { ["line"] = start }, ["end"] = new JObject { ["line"] = end } },
            };

        [Fact]
        public void SelectionContext_null_donne_null() => Assert.Null(IdePrompts.SelectionContext(null));

        [Fact]
        public void SelectionContext_selection_vide_donne_ide_opened_file()
        {
            var r = IdePrompts.SelectionContext(Sel("", true, 3, 3));
            Assert.StartsWith("<ide_opened_file>", r);
            Assert.Contains("C:\\src\\Foo.cs", r);
            Assert.EndsWith("</ide_opened_file>", r);
        }

        [Fact]
        public void SelectionContext_texte_vide_meme_non_vide_donne_ide_opened_file()
            => Assert.StartsWith("<ide_opened_file>", IdePrompts.SelectionContext(Sel("", false, 1, 2)));

        [Fact]
        public void SelectionContext_selection_donne_ide_selection_avec_lignes_plus_un()
        {
            var r = IdePrompts.SelectionContext(Sel("int x;", false, 4, 9));
            Assert.StartsWith("<ide_selection>The user selected the lines 5 to 10 from C:\\src\\Foo.cs:\nint x;", r);
            Assert.EndsWith("</ide_selection>", r);
        }

        [Fact]
        public void SelectionLabel_null_donne_null() => Assert.Null(IdePrompts.SelectionLabel(null));

        [Fact]
        public void SelectionLabel_vide_donne_le_nom_du_fichier() => Assert.Equal("Foo.cs", IdePrompts.SelectionLabel(Sel("", true, 0, 0)));

        [Fact]
        public void SelectionLabel_une_ligne() => Assert.Equal("Foo.cs:8", IdePrompts.SelectionLabel(Sel("x", false, 7, 7)));

        [Fact]
        public void SelectionLabel_plusieurs_lignes() => Assert.Equal("Foo.cs:8-12", IdePrompts.SelectionLabel(Sel("x", false, 7, 11)));

        [Fact]
        public void ExceptionPrompt_utilise_la_premiere_trame_avec_fichier()
        {
            var state = JObject.Parse(@"{breakReason:'ExceptionThrown',
                exception:{type:'System.InvalidOperationException',value:'boum'},
                location:{file:'loc.cs',line:1,function:'Loc'},
                callStack:[{function:'Framework.Throw'},{function:'App.Run',file:'App.cs',line:42}]}");
            var p = IdePrompts.ExceptionPrompt(state);
            Assert.Contains("(ExceptionThrown) : System.InvalidOperationException — boum.", p);
            Assert.Contains("Position : App.cs:42 dans App.Run.", p);
            Assert.DoesNotContain("loc.cs", p);
            Assert.Contains("- Framework.Throw\n", p);
            Assert.Contains("- App.Run (App.cs:42)\n", p);
        }

        [Fact]
        public void ExceptionPrompt_sans_pile_utilise_location_et_sans_exception()
        {
            var p = IdePrompts.ExceptionPrompt(JObject.Parse("{breakReason:'Breakpoint',location:{file:'a.cs',line:3,function:'F'}}"));
            Assert.Contains("(Breakpoint).\n", p);
            Assert.Contains("Position : a.cs:3 dans F.", p);
            Assert.DoesNotContain("Pile :", p);
        }
    }
}
